# Ask-user clarification

**Status:** Latest Enter/Other/decline UX implemented and validated (section 22): 46 focused tests, 4,317 full-suite cases, and six real GPT-6.1-Sol terminal runs. Earlier Ctrl+S/Escape-hide behavior below is historical and superseded.
**Delivery track:** Maintenance.
**Prerequisites:** Current authorized checkout; existing tool pipeline, run steering, session event persistence, and TuiKit interaction owner. Follow [planning governance](planning-governance.md) and [shared context](00-shared-context.md).

## 1 Objective

Provide model-callable blocking clarification with a responsive terminal, explicit answers, and optional attention bell. An answer never grants authority.

## 2 Architectural Context

Reuse `ToolInvocationPipeline`, `SessionApplication`, `RunSteeringCoordinator`, `IDomainEventStream`, `SessionRestorer`, `InteractionCoordinator`, and the TuiKit modal/composer/input owner.

## 3 Scope

Default-off through shared tool controls; parent-only requests with one to three questions, optional single-choice options and text, live suspension, Enter submission and explicit decline/resume, bounded durable lifecycle, interrupted recovery, and trusted host bell preferences.

## 4 Non-Scope

No Slack, native notifications, automatic durable continuation, child questions, security hardening, memory scoring, or unrelated changes.

## 5 Current State

Implementation starts from clean `main` at `3e47286b81e0646bd7725f3c8ba0aed798127048`, equal to fetched `origin/main` on 23 October 2026. No old worktree is used. No commits or pushes are authorized.

## 6 Proposed Design

The compiled tool persists admission and returns a typed suspension. The conversation loop waits after provider enumeration and the tool source lease end, preserving original tool correlation. Steering owns the quiescent question wait. A separate question modal reuses the existing frame, composer, transcript safety, clipboard fencing, and UI dispatch. Explicit answers persist before waking the loop. Restore appends Interrupted for pending or answered-but-unconsumed requests; no provider stack or effects are replayed.

## 7 Public Contracts

Core question/option/answer/request/result DTOs, lifecycle events, admission boundary, and optional `IUserQuestionSurface`. Limits: one to three questions, 64-character IDs, 2,000-character prompts, zero or two to eight options with 300-character labels, 16 KiB serialized question payload; 8,000 characters per answer and 24 KiB total.

## 8 Project/File Changes

Core contracts/catalog; Tools admission/pipeline; Execution conversation/run coordination; Persistence event recovery; Interaction modal arbitration; TuiKit modal and attention; App composition; CoreRuntime and ModelTooling regression tests; prompt and operation documentation.

Changed files (all uncommitted):

- `.threadsmith/config.example`
- `docs/implementation-plans/README.md`
- `docs/implementation-plans/acceptance-scenarios.md`
- `docs/implementation-plans/manual-test-plan.md`
- `docs/operations/prompts.md`
- `docs/operations/tools.md`
- `docs/prompt-file-reference.md`
- `docs/user-guide.md`
- `src/Threadsmith.App/ApplicationComposition.cs`
- `src/Threadsmith.App/HostFoundation.cs`
- `src/Threadsmith.App/InteractiveFrontendRunner.cs`
- `src/Threadsmith.Core/ApplicationContracts.cs`
- `src/Threadsmith.Core/Events.cs`
- `src/Threadsmith.Core/PromptContracts.cs`
- `src/Threadsmith.Execution/InMemoryProjectionStore.cs`
- `src/Threadsmith.Execution/RunSteeringCoordinator.cs`
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`
- `src/Threadsmith.Execution/SessionApplication.cs`
- `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.Toggles.cs`
- `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs`
- `src/Threadsmith.Interaction/Presentation/InteractionOperationActivities.cs`
- `src/Threadsmith.Persistence/SessionRestorer.cs`
- `src/Threadsmith.Tools/ConversationToolAvailability.cs`
- `src/Threadsmith.Tools/ToolContracts.cs`
- `src/Threadsmith.Tools/ToolInvocationPipeline.cs`
- `src/Threadsmith.Tools/ToolStateManager.cs`
- `src/Threadsmith.Tui.TuiKit/ActiveInputLease.cs`
- `src/Threadsmith.Tui.TuiKit/ChoiceModal.cs`
- `src/Threadsmith.Tui.TuiKit/ToggleModal.cs`
- `src/Threadsmith.Tui.TuiKit/TuiKitSurface.cs`
- `tests/Threadsmith.Architecture.Tests/SecurityPolicyConfigurationTests.cs`
- `tests/Threadsmith.CoreRuntime.Tests/Milestone1Tests.cs`
- `docs/implementation-plans/maintenance-ask-user.md`
- `src/Threadsmith.Core/UserQuestionContracts.cs`
- `src/Threadsmith.Execution/UserQuestionCoordinator.cs`
- `src/Threadsmith.Interaction/Contracts/IUserQuestionSurface.cs`
- `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.Questions.cs`
- `src/Threadsmith.Telemetry/UserQuestionSanitizer.cs`
- `src/Threadsmith.Tools/AskUserTool.cs`
- `src/Threadsmith.Tools/Prompts/Tool-ask_user-Description.md`
- `src/Threadsmith.Tui.TuiKit/QuestionModal.cs`
- `src/Threadsmith.Tui.TuiKit/TuiKitSurface.Questions.cs`
- `tests/Threadsmith.CoreRuntime.Tests/ChoicePromptTests.cs`
- `tests/Threadsmith.CoreRuntime.Tests/ToggleNoticeTests.cs`
- `tests/Threadsmith.CoreRuntime.Tests/UserQuestionSanitizationTests.cs`
- `tests/Threadsmith.CoreRuntime.Tests/UserQuestionSurfaceTests.cs`
- `tests/Threadsmith.CoreRuntime.Tests/UserQuestionTests.cs`
- `tests/Threadsmith.ModelTooling.Tests/AskUserConversationTests.cs`
- `docs/operations/memory-intelligence-routing.md`
- `docs/operations/security-policy.md`
- `src/Threadsmith.App/SecurityPolicyConfigurationLoader.cs`
- `src/Threadsmith.Core/EffectiveSessionPolicy.cs`
- `src/Threadsmith.Tools/ToolPolicy.cs`
- `src/Threadsmith.Tools/ToolRegistry.cs`
- `tests/Threadsmith.Architecture.Tests/RepositoryMemoryBindingTests.Archeology.cs`
- `tests/Threadsmith.Architecture.Tests/RepositoryMemoryBindingTests.History.cs`
- `tests/Threadsmith.CodexProvider.Tests/Plan50OpenAiCodexTests.cs`
- `tests/Threadsmith.CodexProvider.Tests/Threadsmith.CodexProvider.Tests.csproj`
- `tests/Threadsmith.ModelTooling.Tests/SharedToolEnablementTests.cs`

## 9 Ordered Tasks

Verify checkout; define bounded contracts; integrate persistence and tool suspension; connect live loop and run lifetime; implement modal and host-only bell; exercise tests and review final diff. See Scenario ASK-USER and MTP-ASK-USER.

## 10 Initial implementation validation (before review)

Validation environment: Windows, .NET SDK 10.0.402, authorized checkout on `main` at `3e47286b81e0646bd7725f3c8ba0aed798127048`. A final `git fetch origin` succeeded; HEAD and `origin/main` remain equal (0 ahead, 0 behind).

- `dotnet restore src/Threadsmith.sln --verbosity minimal`: passed before implementation validation.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore -v quiet`: final build passed, zero warnings/errors (16.47 seconds).
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.CoreRuntime.Tests.ChoicePromptTests`: three passed on final code, covering narrow/standard/wide targets, scrolling, control escaping, resize and cancellation.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.ModelTooling.Tests.AskUserConversationTests`: nine passed, including native/fallback answer/cancel, run cancellation, headless/child rejection, default-off advertisement, forced disabled invocation and Tools readiness.
- Earlier focused CoreRuntime `UserQuestionTests` (six), `UserQuestionSurfaceTests` (two), and `Milestone1Tests` (272) passed; final full-suite validation includes them again.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: final run passed: 4,218 passed, 62 skipped, zero failed (4,280 total; 3m 57.601s). Output at `C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-release-validation.log`.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`: passed, 116 packaged documents / 1,216,616 bytes.
- `git diff --check`: passed, with Git's normal CRLF normalization notices for two documentation catalogs.

The preceding full run had 4,280 tests: 4,217 passed, 62 skipped and one failed new narrow-screen assertion. The assertion assumed the URL suffix stayed on the final scroll page; it now verifies reachability by scrolling. Before that, three existing modal spacing tests caught missing heading spacing; the implementation was corrected. These failed runs are retained in the Temp logs and are not claimed as passing final validation.

Earlier scoped `dotnet format analyzers ... --severity info` returned informational findings, including existing code; new tool/property/helper naming findings were corrected. Scoped whitespace validation found existing compact initializers and new test formatting; the new test was formatted, while verified unchanged initializers were left alone. No claim of a clean whole-repository informational lint run is made.

Skipped manual/platform checks: audible sound confirmation, desktop focus/minimization, live terminal resize, macOS/Linux terminals, SSH/tmux, real clipboard integration and screen-reader behavior. Native notifications are deferred. Conditional suite skips cover opt-in live provider/MCP fixtures, staged embedding/reranking, performance fixtures, platform filesystem cases, unavailable symlink privileges and missing ripgrep. No dependencies or OS settings were installed/changed to enable these cases. The no-network live URL probe tests actual built modal presentation, not a web request or a security-policy decision.
## 11 Security/Permissions

No answer invokes an approval API or writes memory. Existing tool policy, immutable policy generation, repository identity, and child limits remain authoritative. Controls and oversized data are rejected. Audio uses only trusted host configuration and the frontend backend.

## 12 Observability

Correlated durable question state and metadata-only waiting events accompany the original tool lifecycle. Question/answer bodies do not enter telemetry. Persistent terminal waiting status exposes cancel/mute controls; declined questions leave no hidden pending UI.

## 13 Migration/Compatibility

Additive version-1 event types use the existing database and retention ownership. Older sessions without question events are unchanged. Restart interrupts incomplete live questions. Headless callers receive no advertised question tool.

## 14 Acceptance Criteria

An explicit answer resumes the original call once; Escape/Cancel returns the host-owned refusal message and resumes once; no provider/source permit spans the human wait; mixed batches execute nothing; restart does not replay effects; current policy is checked before resuming.

## 15 Risks

Review traced the ordinary provider enumeration, batch preflight, source lease disposal, original call correlation, persisted terminal selection, run cleanup, and shared terminal input owner. Real-input tests exposed modal-before-key-filter precedence; cancellation/mute now live on the question modal. Selection and question presentation share a surface gate; closed modals are removed before queued question admission. A bounded run ledger retains prior invocation identities. Interrupted waits close the existing tool completion/hook lifecycle. Live Windows question paths subsequently passed with explicit user authorization, as recorded below.

## 16 Documentation

Tool operations, terminal preference guidance, both prompt references, Scenario ASK-USER, and MTP-ASK-USER own their respective user-facing contracts.

## 17 Open Decisions

Live validation used the supported `Threadsmith.App.exe --tui=tuikit --repository <disposable Temp directory> --trust UntrustedInspection --set:persistence:path=<test live.db>` workflow in an interactive Windows ConPTY console (80 x 24). `TERM=xterm-256color` and `THREADSMITH_tui__questions__audio=bell` were set only for the child process. The first launch with inherited `TERM=dumb` correctly rejected the terminal. The configured default model endpoint then failed transport after three built-in attempts. The existing Codex model was selected only for the disposable repository, and returned a real response stating the question tool was unavailable. Inspection confirmed explicit trusted `tools.allow=[]`, which the current policy compiler intentionally treats as no allowed tools. That initial attempt did not widen policy.

Automatic approval review initially rejected the model-selector Enter because persistence scope was uncertain. Read-only inspection established that composition passes the disposable repository configuration path to `ActiveModelSelectionService`, and its persistence writes only that repository's model fields. One retry was then accepted. No denial remains pending. `/quit` exited the test process with code 0; the pre-existing user process was left running. Test artifacts remain under `C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-live-827bc29794054f368ebb6419f615ea77`.

The shipped example omits `ask_user` from enabled/allow lists. The review follow-up removes the separate `tools:askUser:enabled` gate in favor of the existing `EnabledByDefault=false` tool contract and shared Tools preferences; ordinary trusted policy remains independent. Audio remains opt-in.

Native notification delivery is deferred. Persistence reuses versioned session events rather than introducing a second store or continuation engine. Full same-call recovery across restart remains outside this release.


## Final scope update and live evidence

The user authorized temporary enablement for testing, then required default-off behavior. The only temporary personal policy change was adding ask_user to tools.allow in C:\Users\mwrig\.threadsmith\config.json. A byte-preserving backup was created at config.json.ask-user-backup-20261023T025407Z before editing. Cleanup removed only that temporary entry while preserving unrelated edits; the backup was not restored wholesale.

The supported built executable ran under Windows ConPTY (80x24) against the disposable repository and SQLite database live-enabled.db. The existing OpenAI Codex GPT-6.1-Sol profile answered a selected blue option, and then received the exact two-line text after Escape/F4 hide/reopen. F5 produced a cancellation response; Ctrl+C closed the modal, cancelled the run and restored the composer. One question BEL was emitted on presentation and none on reopen; audible sound cannot be verified remotely. Personal audio settings were unchanged.

SQLite evidence: four question requests, four matching tool completions, terminal outcomes Answered, Answered, Cancelled, Interrupted. Between each Pending and terminal event the only event was userQuestionReady: no model output or context assembly occurred while waiting. Each original ToolInvocationId completed exactly once. Three normal/cancelled answers have a consumed record; the interrupted run does not pretend to have delivered an answer.

The related approval-modal fix uses the existing safe TranscriptView for wrapped prompt text and full F2 details, including unbroken URLs. Wider terminals allow a 120-column frame, bounded by the shared margins; narrow/resize layouts retain option navigation and cancellation. Query values remain hidden by the existing web-approval privacy contract; no policy or target reconstruction changed. A live-hidden question exposed an animated running activity; Pending now closes that activity while the persistent waiting status remains authoritative.

Final enablement cleanup completed: only the temporary ask_user entry was removed from user tools.allow, returning it to []. No tools:askUser:enabled value exists in the user file, and process/user/machine environment values are absent. A fresh live process displayed Ask user with the full disabled-by-default reason and user opt-in instructions in Tools/F2. All disposable live processes exited normally; the pre-existing user session was untouched. A second live run with process-scoped opt-in verified a correlated text answer and a stable hidden waiting status after the activity fix.

Final review traced source permit disposal, provider iterator disposal, the original call correlation, persist-before-wake ordering, terminal races, repository/policy fencing, child/delegation rejection, frontend teardown, input arbitration, paste identity fences, default-off advertisement/direct admission, trusted configuration provenance and ordinary tool-policy conjunction. URL approval identities and outcomes are unchanged; the added full details are display-only. No independent continuation engine, raw input pump, memory writes or new security approvals were introduced.

A disposable ModalProbe (under the existing Temp test directory) loaded the actual built TuiKit/Interaction assemblies and presented a benign example.test origin with a 350-character unbroken path. Live Windows ConPTY output showed wrapped text and the complete VISIBLE_TARGET_END suffix in F2 details. Escape returned IsCancelled=True; the probe performed no network operation, model call, or policy change. Responsive 40x12/80x24/160x40 and resize behavior is tested automatically; live resize and other emulators were not exercised.

The final user configuration hash equals the original pre-test backup after targeted cleanup. Final repository file manifest contains 43 changed/untracked files; none are staged. Existing security, memory and intelligence behavior remains subject to the original regression suite, with no unrelated fixes.

Initial implementation outcome (superseded by section 18): all 4,280 tests completed, with 4,218 passes, 62 conditional skips and no failures. No current authorization blocker remains. Native OS notifications and cross-platform/manual checks listed above remain deferred; no durable same-call restart resumption is claimed. No staging, commits or pushes were performed.


## 22 Latest UX contract: Other, Enter and decline (validated)

This section supersedes all historical Ctrl+S/Enter-newline/Escape-hide instructions in sections 18–21. The exact heading is The Assistant Has a Question, followed by one intentional blank line. Choices append a harness-owned numbered Other; only selecting Other reveals/focuses an editor, and its text uses the existing null-option/text contract. With eight model choices Other is number 9. Questions without choices directly show an editor. Digits type normally there; pasted multiline text does not submit.

Enter explicitly selects the focused choice and submits, or submits editor text. It also submits from full details. Incomplete groups identify and focus the missing question with an actionable message. PgUp/PgDn navigates pages; existing focused Previous/Next actions use Space so Enter retains submission semantics. Escape, explicit Cancel and F5 all resolve the existing Cancelled result with the exact host-owned Message: The user declined to answer. The ordinary conversation continuation handles it once. Ctrl+C/run cancellation remains Interrupted and does not fabricate a refusal. No hidden pending UI or F4 reopen remains.

Reuse: unchanged ModalFrame, shared modal/input owner, composer, text wrapper, transcript details, question coordinator, sanitizer and pipeline. Other is only a boolean in the UI draft, not a model option or new persistence schema. The existing typed UserQuestionResult now exposes the deterministic cancellation Message; no model-provided text is injected into this message. Removed the competing hidden-state input path. The previously modified system prompt was observed at turn start and preserved, along with all other work. Branch remains feature/user-question-tool; no personal settings or installed binaries changed.

The new screenshot Library read did not return inspectable pixels. No pixel inspection is claimed; live rendered terminal output will be inspected independently. Final build passed: `dotnet build src/Threadsmith.sln --configuration Debug --no-restore -v quiet`, zero warnings/errors (6.89s). Focused tests: `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class 'Threadsmith.CoreRuntime.Tests.UserQuestion*'`: 25 passed, zero skipped. `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.ModelTooling.Tests.AskUserConversationTests`: 21 passed, zero skipped. These exercise eight choices plus ninth Other, initial focus, small layouts, group validation, all five decline routes through real terminal bytes, secret sanitization, native/fallback/case variants, original correlation, once-only completion, and separate run interruption. The full sequential suite passed; live results and final counts follow. Packaged docs preflight passed 116 files.

Live Windows ConPTY (80x24, no-color) using GPT-6.1-Sol, the built Debug application, and disposable enter-decline-live.db: verified the exact heading/blank line and choices-only initial view; pressed 3 for Other, observed the editor appear, typed Somewhere in between 12, then Enter. The actual provider tool result had OptionId null and that exact text; the model repeated it. A no-choice question directly opened the editor and Direct answer 42 + Enter reached the model. Escape from choices yielded Cancelled/empty Answers/Message The user declined to answer; GPT acknowledged it and the ordinary composer accepted the next test request. Escape after selecting Other and typing Unsubmitted draft produced the same refusal without the draft text; the composer became usable again. No F4 reopening or hidden wait occurred. Evidence is the synthetic-session host-to-provider log C:\Users\mwrig\AppData\Local\Temp\threadsmith-question-enter-decline-live.jsonl, not TLS interception.

Further live checks passed: Tab twice focused the explicit Cancel action; Enter returned the same Cancelled/empty Answers/refusal Message and the model acknowledged it. For a two-question request, Down+Enter selected Blue and moved to Explanation with visible feedback Answer question 2, then Enter. Bracketed paste of Because blue plus newline plus feels calm remained pending; Enter submitted both answers and the provider result preserved the newline with no synthetic option ID. The model acknowledged both. All six live runs had exactly two provider requests (initial tool call and one post-resolution continuation); none retried ask_user. The composer accepted each following test turn. `/quit` exited the disposable process 0.

Test-only distinctions: ninth Other, Escape inside F2 details, F5, secret redaction, actual input-owner resize and policy/case/interruption races are automated coverage; they were not all repeated in a native desktop window. Windows ConPTY was observed, not macOS/Linux or a physical resize gesture. No screenshot pixels, screen reader, mouse option selection, or audible sound claim. Personal configuration and installed binaries were not changed. The existing user system-prompt edit remains untouched.

Final full-suite command: `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 1`. Result: **4,317 total, 4,255 passed, 62 skipped, zero failed**, exit 0, 8m 50s 893ms. Log: `C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-regressions\question-enter-decline-final.log`. No C# or prompt asset changed after the final build. Final documentation preflight passed 116 files; `git diff --check` passed. Conditional skips retain the existing platform/assets/live-fixture limits; this is Windows validation only.

Final review traced Escape/Cancel through the existing modal completion, AskAsync finally cleanup (draft/read-only state, modal and semaphore), coordinator persistence-before-wake, original tool continuation and policy checks. Terminal results and six two-request live runs support once-only completion; true run interruption remains separate. No pending/denied/uncertain operation remains. Changes are uncommitted on feature/user-question-tool at base 3e47286b81e0646bd7725f3c8ba0aed798127048. The installed application was not replaced; the rebuilt application contains the fix.

## 21 User-reported question navigation/layout regression

The user's failure was real in the current source, not explained away as an outdated installation. `QuestionModal` initialized `_focus` to zero (details), so arrows scrolled the transcript instead of navigating choices. Option labels lived in a static details transcript; even after Tab, moving the option changed a separate summary rather than highlighting that visible option row. The fixed details-height allocation left unused rows around short questions. Number keys were not implemented at all.

The previous successful live sequence was Tab, Space, Ctrl+S for Great, and Tab, Down, Space, Ctrl+S for Lousy. It proved correlated answer delivery after changing focus, not natural opening-to-arrow navigation. Reporting that as complete interaction validation overstated its coverage. The earlier automated test also explicitly tabbed past the initial details focus and missed the same issue.

The correction uses the existing Modal input dispatch, ModalFrame geometry, TUIKit TextWrapper, CachedTextRun/semantic theme roles, TranscriptView full details, and TuiKitComposer. No input reader, event loop, persistence path, or approval mechanism was added. Options now own initial focus; `> [ ] 1. Label` separates focus from explicit `[x]` selection. Up/Down moves focus; Space or a displayed 1–8 selects without submitting. Digits apply only in option focus and only to the current question; they type normally in the editor. Enter does not select/submit while option navigation owns focus, preventing opening-key carryover. Ctrl+S or the explicitly focused Submit action submits the validated group. Tab skips nonexistent options and single-question page actions. Page changes focus the new answer control; reopening restores its selected option or editor draft.

The short prompt immediately follows the title and the options immediately follow the prompt. Prompt height is bounded by wrapped content instead of half the frame. Focused option rows use the existing SelectionHighlight role plus a non-color marker. F2 provides all safely escaped prompt/option text with existing transcript scrolling, including long labels clipped in the compact list. Context-specific hints explain option selection, editor newlines, and action activation. Shared ChoiceModal spacing and approval semantics are unchanged.

Installed `C:\Program Files\Threadsmith\Threadsmith.Tui.TuiKit.dll` had timestamp 2026-10-23 12:32:37 UTC and SHA-256 `748D1D5141FA31E8A710DA11AEA6E62918DE2B442F9AE15003F0C3D0C54A87A6`. Its hash differs from Debug; matching version stamps do not prove source equivalence. No installed file or user process was changed. The defect is independently present in the source used for our prior tests. The Library read did not provide inspectable screenshot pixels; parent OCR is only a diagnostic clue, not visual verification. Terminal output below was observed directly in disposable ConPTY sessions.

Live Windows 80x24, no-color, TuiKit/GPT-6.1-Sol verification: opening displayed `> [ ] Great!`; Down alone moved `>` to Lousy while leaving `[ ]`; Space changed it to `[x]` without resuming the model. Escape hid the modal, F4 restored the checked Lousy choice, and Ctrl+S delivered it; the model acknowledged Lousy. A two-question request then selected Blue, used Tab to Next, entered `Blue feels calm` plus Enter plus `Second line`, hid/reopened preserving both lines and page, and submitted both answers; the model acknowledged Blue and the text. These sessions used disposable files/database and exited normally. Logs: `C:\Users\mwrig\AppData\Local\Temp\threadsmith-question-navigation-live.jsonl`.

Final numeric-build live verification: opening then `2` displayed `> [x] 2. Lousy.`; `1` changed selection to `> [x] 1. Great!`. Invalid `9` and Enter did not submit. Down moved focus to unanswered Lousy while Great remained checked, demonstrating separate focus and selection. Up then Ctrl+S submitted Great, acknowledged by GPT-6.1-Sol. A second request used Tab, `12`, Enter, `34`; both digits remained text on separate lines. F2 full details and F2 return preserved that draft. Ctrl+S produced a correlated tool result with `OptionId: null` and `Text: "12\n34"`. Evidence: `C:\Users\mwrig\AppData\Local\Temp\threadsmith-question-numeric-live.jsonl`. These are real terminal keystrokes and host-to-provider requests, not direct host-answer commands or intercepted HTTP/TLS. Native mouse option clicking is not implemented by this question modal (nor required by its keyboard contract); it was not claimed tested. Resize was exercised through real terminal-byte dispatch with the HeadlessBackend resized 80x24 → 40x15 → 100x30, plus rendered 40x15/80x24/160x40 cases, not by resizing a physical terminal window. No macOS/Linux, screen-reader, or audible-bell validation is claimed. Both disposable live sessions exited 0; personal configuration was verified semantically unchanged by this follow-up. Packaged documentation preflight passed 116 files and git diff --check passed.

Final validation: `dotnet build src/Threadsmith.sln --configuration Debug --no-restore -v quiet` passed with zero warnings/errors (23.48 seconds). `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class 'Threadsmith.CoreRuntime.Tests.UserQuestion*'` passed 19/19, zero skips. This includes direct render/input tests and the existing real input owner, sanitizer and lifecycle tests. An earlier CoreRuntime run before numeric changes passed 769 with four skips; that is historical, not final-code validation. The final full suite uses `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 1` and logs to `C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-regressions\question-navigation-final.log`.

Final full-suite result: **4,311 total, 4,249 passed, 62 skipped, zero failed**, exit 0, 7m 51s 013ms. This covers the final numeric-selection code. No C# changed after the final build. The focused command executed 19 tests (not zero). Six new cases cover initial navigation at three sizes, compact multiple questions, numeric/edit focus boundaries, and real terminal-byte resize/reopen. No outstanding execution blocker; limitations are the unsupported mouse selection and unverified physical-terminal resize, screenshot pixels, other platforms and audible audio described above. These edits remain uncommitted in the user's ordinary checkout; deployment was not performed.

## 20 Latest review: global inventory and common enablement (validated)

The reported Inference configuration explicitly enables ask_user in ordinary `tools.defaultEnabledOverrides`. Its personal `C:\Users\mwrig\.threadsmith\config.json` had `tools.allow: []` before the explicitly approved removal below. Machine config `C:\ProgramData\Threadsmith\config.json` and Inference session.json are absent; this executor has no tools.allow environment override. Only the subsequently approved empty tools.allow removal changed personal settings, as documented below.

An isolated Windows ConPTY reproduction used the prior passing Debug executable, GPT-6.1-Sol, FullyTrustedAutomation, a disposable repository and inherited unchanged policy. Its captured request to the provider contained zero tools. The real model reported ask_user, read_file and write_file unavailable. The process exited normally. Evidence is in `C:\Users\mwrig\AppData\Local\Temp\threadsmith-tool-inventory-request.jsonl`; only this synthetic session was logged. This is the host-to-provider request, not a captured TLS/HTTP body. The Codex adapter maps its current request tools directly into Responses function schemas; serialization is independently tested at its HTTP handler boundary. This negative reproduction preceded the approved configuration correction and positive live verification below.

History establishes a pre-ask-user compatibility change: commit `79b29c6` replaced legacy `AllowedToolIds.Count > 0` enforcement (empty meant unrestricted) with explicit default-vs-empty immutable policy (empty means deny-all). The security plan required a targeted migration diagnostic for explicitly supplied legacy empty filters. That diagnostic was missing. The compiled missing-field default is already unrestricted; no automatic migration can infer whether an explicit user empty list is obsolete or intentional. The new diagnostic identifies the source and recommends deliberately removing only an obsolete key. Policy semantics are not weakened.

The user's running PID 109400 was `C:\Program Files\Threadsmith\Threadsmith.App.exe`. Installed Tools and Execution DLL hashes differ from Debug binaries; both report file version 0.1.0.0, so file version alone cannot identify the build. No installed files or running user processes were modified. New source fixes require a rebuilt deployment; this agent has not installed one.

Fresh `git fetch origin` completed without merging or changing the checkout. Remote main remains `3e47286b81e0646bd7725f3c8ba0aed798127048` (security-policy merge on October 22, 19:15 -0400). Both installed and Debug App product versions embed this base hash; uncommitted differences are not represented by that stamp. Same-input source comparison shows identical explicit-empty allowlist resolution and rejection in remote main versus the pre-follow-up feature code. No older baseline checkout was created or built.

Config audit (read only, allow-field shape and file hashes): the pre-removal user config had the same SHA-256 as `config.json.ask-user-backup-20261023T025407Z`. Both contain explicit empty tools.allow. Earlier backups `config.json.backup-20261021T135249608Z` and `config.json.before-read-file-5mib-20261018-152601.bak` also contain the empty array; the latter retains an October 12 timestamp. The empty filter predates the ask-user live-test edits. Timestamps do not identify an actor; no claim is made that the user introduced it. Cleanup restored the pre-test bytes, but the later security contract changed their interpretation.

### Shared ownership audit

| Category / entry | Generic enablement owner | Distinct constraints / changes |
|---|---|---|
| Built-in, default-off, ask_user | `ToolStateManager.EnableAsync/DisableAsync`, `IsEnabledCore`, `PersistAsync`; `ToolRegistry` | Default-off descriptor metadata only. Question capability/schema/quiescence/correlation are question behavior, not preference switches. |
| UI Tools | `ApplyToolToggleAsync` delegates to the same manager | Shared policy-denied reason now explains checked-but-excluded preferences; checkbox does not rewrite trusted policy. |
| MCP management/API | `McpManager` delegates version-fenced enable/disable to the same manager | Exact capability digest approval, source connection and consent remain prerequisites. |
| Extensions/dynamic sources | `ToolRegistry.RegisterOrReplace/Remove` register/unregister metadata in the same manager | Loading/unloading is source lifetime, not another tool preference store. |
| Intelligence/archeology | Individual toggles now use normal preference persistence | Removed callback redirection to feature master enablement. `/intelligence` still owns shared service lifetime; readiness is displayed independently. |
| Intelligence history tool calls | Registry now honors common tool preferences | Removed the internal history-reader bypass. Required Git readers must be enabled; ordinary policy and intelligence restrictions still apply. Direct non-tool host collection is not model advertisement. |
| Root/native/fallback | `ConversationToolAvailability.CreateSnapshot` consumes enabled registry registrations on each round | Trust, immutable policy, source readiness, schema and transport constraints remain separate. |
| Child | Parent registration snapshot plus `AgentToolPolicy` intersection and `SubagentAvailable` | Child cannot widen parent authority or ask unsupported interactive questions. |
| Codex transport | Current `ModelStreamRequest.Tools` canonicalized and serialized per HTTP request | No independent enablement store or app-server dynamic-tool registration; provider-specific schema formatting does not decide enablement. |
| Rebind/persistence | `ToolStateManager.BindRepositoryAsync` reloads the same preference fields | MCP approvals retain repository/version identity; extension source identity remains fenced. |

The immutable named-tool ceiling is now one `EffectiveSessionPolicy.AllowsTool` predicate reused by advertisement, execution and Tools diagnostics. This removes duplicated policy checks without merging permission into preferences. Generic registry Get uses the same enabled-tool resolver. No additional ask-specific enablement option was introduced.

Current validation: final build passed (zero warnings/errors, 10.48s); SharedToolEnablementTests 4/4; complete CodexProvider project 61 passed/2 conditional skips; SecurityPolicyConfigurationTests 47 passed/3 skips; RepositoryMemoryBindingTests 58/58. The first lifecycle run failed five tests that depended on disabled Git-reader bypass; fixtures now explicitly enable readers and assert root/child rejection while disabled. The final sequential full suite passed: 4,305 total, 4,243 passed, 62 skipped, zero failed (6m 17s 080ms). Documentation preflight passed all 116 files; diff whitespace check passed.

First broader run: 4,305 total, 4,242 passed, 62 skipped, one failure, 7m 34s 915ms. `ToolStateManagerTests.ToggleAsync_PersistsJsonWithCommentsAndTrailingCommas` encountered `UnauthorizedAccessException` at the unchanged atomic `RepositorySettingsCoordinator` File.Move path while replacing a disposable config. All seven ToolStateManagerTests passed immediately on rerun with fresh fixtures and no code changes (948ms). The external access-denial cause was not established; no retry/timeout workaround was added to production. The full confirmation run passed with no code changes, logged separately at `C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-regressions\shared-enable-regression-confirmation.log`; the failed run remains in `shared-enable-regression.log`.

Exact focused commands use `dotnet test --project <project> --configuration Debug --no-build`: ModelTooling with `--filter-class Threadsmith.ModelTooling.Tests.SharedToolEnablementTests`; the complete CodexProvider project; Architecture with `--filter-class Threadsmith.Architecture.Tests.SecurityPolicyConfigurationTests` and separately `Threadsmith.Architecture.Tests.RepositoryMemoryBindingTests`; ModelTooling with `--filter-class Threadsmith.ModelTooling.Tests.ToolStateManagerTests`. Full runs use `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 1`. None of the quoted focused passes were zero-test runs.

Live UX verification with the new Debug build first showed the source-specific PolicyMigrationRequired diagnostic; checked Ask user and Delegate agents both displayed the common policy-denied reason in Tools/F2. That disposable process exited 0.

After explicit user approval, removed only the verified empty `tools.allow` property from `C:\Users\mwrig\.threadsmith\config.json`. Backup: `C:\Users\mwrig\.threadsmith\config.json.before-legacy-allow-removal-20261023T122702Z.bak`. Semantic comparison verified that removing this property from the backup produces the current configuration; all other settings remain unchanged. The Inference `tools.defaultEnabledOverrides` preference still enables ask_user. No installed binaries or active user processes were changed. An already-running process must reload/restart to capture the updated policy; the source UI/diagnostic fixes require the user's normal rebuilt deployment.

Positive live verification used the built Debug TuiKit application, existing GPT-6.1-Sol configuration, FullyTrustedAutomation, and disposable repository `C:\Users\mwrig\AppData\Local\Temp\threadsmith-tool-inventory-live`, with test-local persistence. It read README.md, wrote `.inbox/tool-check.txt`, read it back, then opened the real ask_user modal. The file contained exactly `TOOL_CHECK_OK`. Selecting Great! resumed the model, which acknowledged that answer. Logged assistant/tool messages used the same original `host-tool-4-1` correlation and one Answered result (`mood` / `great`).

In the same process, disabling Ask user in the common Tools dialog removed it from the next request (34 tools to 33), while read_file/write_file remained and a real read succeeded. Re-enabling restored the entry; a further read was followed by an Enter steering pause, Escape dismissal, and the requested question modal. Selecting Lousy resumed the model with its matching acknowledgment. The disposable application then exited normally (code 0). No restart was needed for tool preference refresh. The first attempted disable was cancelled before application; only the later confirmed unchecked/apply state is counted as the disable test.

Positive request evidence: `C:\Users\mwrig\AppData\Local\Temp\threadsmith-tool-inventory-positive.jsonl`. This records the actual host-to-provider catalog and correlated messages, not an intercepted TLS body. HTTP schema serialization is also covered by nonzero Codex adapter tests. Real-model option answers and steering resume were observed in this pass; earlier free-text/hide/reopen/cancel coverage remains documented in the prior live section. No audible bell or macOS/Linux behavior is claimed. No personal conversation database was scanned or migrated.

## 19 Case identity and repeated persistence finding

Verified again in the normal IDE checkout on `feature/user-question-tool`, HEAD `3e47286b81e0646bd7725f3c8ba0aed798127048`. All 48 existing changed/new files are preserved. No former worktree contents, personal database or personal configuration were accessed by this review pass.

P1 is already fixed in this checkout, verified against current source rather than the review's former-worktree line numbers. `UserQuestionCoordinator.cs:63` sanitizes admission before publication at line 103 and retention at 113. `UserQuestionSanitizer.cs:27–28` covers prompts/options; line 51 covers answer text. `UserQuestionCoordinator.cs:190` sanitizes terminal results before publication at 204 and retention at 205. Wait uses the canonical safe request at 135; consumed/interrupted events use that request at 220/238. Cancellation contains no answer text. `SessionRestorer.cs:102–103` sanitizes request/result before projection and appending a new interrupted event at 143; question-error diagnostics are generic. `ApplicationComposition.cs:358` injects the existing host sanitizer. `HostFoundation.cs:408` still persists events directly, so early sanitization is required and covered by tests. Transient unsaved editor drafts are not persisted. Existing old rows are not migrated, scanned or deleted.

P2 was confirmed at `ToolInvocationPipeline.cs:89`: continuation ownership used a case-sensitive tool-name comparison despite the registry's `OrdinalIgnoreCase` dictionary (`ToolRegistry.cs:66`) and batch preflight at pipeline line 149. Continuation and availability now use `OrdinalIgnoreCase`; the suspension-type check remains `AskUserTool`. Both normal completion and interruption cleanup call this common method from `SessionApplication.ConversationLoop.cs:1429/1421`. Opaque request, run, session, invocation and provider call identities retain their exact comparisons.

Expanded tests exposed a second mismatch: the admitted pipeline result used the canonical lowercase name, but the assistant history retained the model's original spelling. `CompleteSuspensionAsync` now returns the original request tool-name spelling at line 105. This preserves exact assistant/result protocol history without weakening its correlation checks. Lowercase, uppercase and mixed-case variants all exercise the actual loop/pipeline in native and fallback modes, explicit answer, cancellation and run interruption, plus exactly one correlated completion. Negative cases reject an unrelated tool name, run or session.

Validation for this pass:

- First focused run executed 21 tests: 13 passed and 8 failed on the history-name mismatch. This was not treated as successful validation.
- Final build: `dotnet build src/Threadsmith.sln --configuration Debug --no-restore -v quiet` passed, zero warnings/errors, 22.13s.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.ModelTooling.Tests.AskUserConversationTests`: 21 discovered/executed, 21 passed, zero skips/failures, 4.418s.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.CoreRuntime.Tests.UserQuestionSanitizationTests`: 5 discovered/executed, 5 passed, zero skips/failures, 5.665s. Synthetic fixtures assert absence of raw sentinels in retained state, event subscriber payloads and SQLite serialized rows, including cancellation/interruption, restore and persistence failure. The legacy fixture deliberately verifies an old row is not silently rewritten.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 1`: 4,299 total, 4,237 passed, 62 conditional skips, zero failures, 11m 56s 475ms. Log: `C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-regressions\case-regression.log`. No product/test edits followed the passing build and test runs.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`: passed, 116 packaged files. `git diff --check`: passed. Final review checked registry/admission/preflight/suspension/completion/interruption identity handling, exact protocol history correlation, and the existing sanitizer-before-persistence paths.

No live user session or personal preference was changed. No new live terminal/model/audio observation is claimed; these fixes are exercised with deterministic providers and disposable SQLite state. Windows-only and earlier platform limitations remain applicable.

## 18 Review follow-up: shared toggles and early redaction

Current checkout: C:\source\repos\Threadsmith on feature/user-question-tool, base 3e47286b81e0646bd7725f3c8ba0aed798127048. The user's branch transition resolved the earlier registered-worktree ownership blocker. All 43 existing implementation files were preserved. No old worktree contents were accessed, and no worktree, branch, commit or push was created by this follow-up.

Root cause 1: AskUserTool captured an immutable enablement flag independently of ToolStateManager. Tools could persist ordinary enablement but could not change that flag, so its checkbox remained disabled. The flag and readiness wrapper are removed. The tool now uses EnabledByDefault=false, the existing shared manager/registry and repository preference persistence, like other tools. Local UI enabling applies immediately; trusted allowlists, denials, trust and approvals remain independent ceilings. The old askUser enabled setting is ignored. This intentionally replaces the earlier special gate; no new preference store or approval bypass was added.

Root cause 2: ToggleModal retained a prior action's reason in its notice row while separately drawing the selected reason in its footer. The notice followed later selections and duplicated details. Selection/filter changes now clear stale notices, identical notice/footer reasons render once, and host reconciliation refreshes the selected option including removal of an obsolete reason. Full F2 details remain scrollable; the details footer shows navigation rather than repeating the reason.

Root cause 3: question admission and terminal selection copied raw text into retained coordinator records and UserQuestionStateChanged before the later tool-result sanitizer ran. HostFoundation persisted those raw events directly. A shared typed sanitizer using the existing IOutputSanitizer now processes requests and answers before retention/publication, with validation before and after and stable IDs preserved (sensitive IDs are rejected). Wait and consumed-event paths use the canonical sanitized request. Restore sanitizes before projection or appending Interrupted records and uses generic question-error diagnostics. Production composition injects the host sanitizer. Unsaved local editor drafts remain transient like ordinary composer input and are never serialized as draft events; accepted answers pass through the host sanitizer.

Prior validation gaps: the previous nine conversation tests verified the standalone config gate but never enabled ask_user using the actual common Tools control. No tests inspected raw persisted clarification payloads for synthetic secrets. A green broad suite did not establish either missing contract. New regressions exercise shared enable/disable/reload, advertisement and forced invocation with ordinary policy denial, selection/repaint/resize, and raw durable/subscriber/retained/restore data with synthetic credentials. Old persisted plaintext may remain in pre-fix question events; this change does not scan, rewrite or delete personal conversations.

Screenshot limit: Library libfile_28b27f1a1b74819181c03b7b06d21dea could not be materialized using the supported helper because Windows Python lacks os.setxattr. The permitted transfer did not produce a local image. No pixel findings are claimed; diagnosis and regression coverage come from current source and execution.

### Follow-up validation and live evidence

- Build: dotnet build src/Threadsmith.sln --configuration Debug --no-restore -v quiet passed with zero warnings/errors (30.64s). The first build exposed only test formatting/analyzer issues, corrected before test execution.
- Focused sequential runs: UserQuestionSanitizationTests 5/5, ToggleNoticeTests 2/2, AskUserConversationTests 9/9 passed. Commands use dotnet test --project with the owning CoreRuntime or ModelTooling project, --configuration Debug --no-build --filter-class and the full class name.
- Full sequential suite: dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 1 passed: 4,287 total, 4,225 succeeded, 62 skipped, zero failures, 11m 01s 558ms. Log: C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-regressions\full-regression.log.
- Live Windows ConPTY 80x24: supported built application, disposable repository C:\Users\mwrig\AppData\Local\Temp\threadsmith-ask-user-regressions\live. In /tools, Ask user started unchecked; Space checked it immediately and persisted defaultEnabledOverrides=[ask_user]. A second Space unchecked it, removed the override and persisted disabled=[ask_user]. Moving to Delegate agents cleared prior reason text. F2 on Ask user displayed the full reason once with navigation hints instead of duplicate footer text. /quit exited normally. No model request or additional tool permission was needed for this live UI check.
- Personal config hash before/after matches. No personal configuration or trusted allowlist was changed, and the disposable test setting was left disabled. The existing user session was not controlled. Narrow and resize coverage is automated; the user's supplied screenshot could not be inspected because of the supported-transfer Windows limitation.

Final focused commands (each passed, run sequentially before the full suite):

```powershell
dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.CoreRuntime.Tests.UserQuestionSanitizationTests
dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.CoreRuntime.Tests.ToggleNoticeTests
dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.ModelTooling.Tests.AskUserConversationTests
```

Final documentation preflight: `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` passed for 116 packaged files. `git diff --check` passed. Final diff review covered common toggle persistence and registry admission, stale modal state, sanitizer ordering through admission/completion/consumption and restoration, and safe exceptional diagnostics. No C# edits followed the passing build/tests. Final branch remains `feature/user-question-tool` at `3e47286b81e0646bd7725f3c8ba0aed798127048`, with HEAD and the locally fetched origin/main equal (0/0). This follow-up did not fetch or pull, so it does not claim a new remote freshness check. All 48 changed/new files remain unstaged in the IDE checkout.

Follow-up limits: Windows only; no macOS/Linux, real clipboard, screen-reader or live resize verification. This follow-up did not rerun a live model question or audible bell; earlier live question observations above remain historical evidence, while the final native/fallback conversation tests passed against current code. The 62 conditional skips include unavailable symlink privileges, ripgrep, live provider/MCP configuration, staged embedding/reranking and performance fixtures. The screenshot transfer remains blocked by the supported helper's Windows limitation. No personal history was inspected or rewritten; pre-fix plaintext may remain in existing database rows. No operational or authorization blocker remains for the implemented fixes. No staging, commits, pushes, alternate checkout creation or personal configuration changes occurred in this follow-up.
