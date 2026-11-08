# Show instructions at the start of each subagent window

**Status:** Implemented. Current Windows build and affected tests pass; the full suite has one unrelated validation-fixture failure. Physical-terminal and other-platform checks remain unverified.
**Delivery track:** Maintenance.
**Prerequisites:** The existing child runner, delegation lifecycle, agent workspace projection, and TUIKit child transcript.

Read [AGENTS.md](../../AGENTS.md), the [C# guardrails](../guardrails/portable-csharp-guardrails.md), and [planning governance](planning-governance.md) before implementation.
Use the structure in [shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).

## 1 Objective

Show the instructions passed to each subagent at the start of its output window.
Let the user see the assigned work before the subagent's response, tool output, or failure details.

Use [ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf) for this plan.
Keep software names and code identifiers in their exact forms.

## 2 Architectural Context

Use the existing execution and presentation paths:

`child runner -> domain event stream -> AgentWorkspaceProjection -> child transcript`

| Component | Existing responsibility |
|---|---|
| `DelegateAgentsPlanning` | Freezes each assignment and expands shared context. |
| `ChildAgentPrompt` | Builds host, role, repository, assignment, and evidence messages. |
| `ChildAgentModelLoop` | Selects the effective model and prepares each request. |
| `DelegationOrchestration` | Owns accepted child identities and lifecycle events. |
| `AgentWorkspaceProjection` | Routes child events and display text to the correct window. |
| `AgentViews` and `TranscriptView` | Retain and display each child's output. |

Preserve [ADR-62](../architecture/adr-62-sole-tuikit-interactive-frontend.md) and the [delegation contract](../architecture/delegate-agents-tool.md).
Use the existing [shared context contract](maintenance-delegation-shared-context.md).
Preserve [Scenario L](acceptance-scenarios.md#scenario-l--bounded-parallel-research-and-review) and [Scenario AU](acceptance-scenarios.md#scenario-au--tuikit-agent-workspace).

## 3 Scope

Include every subagent role that uses the existing child execution path.
Include model-requested delegation and internal callers that use that path.
Include selected windows, unselected windows, concurrent children, failure, cancellation, and replacement generations.

Use these terms:

| Term | Meaning |
|---|---|
| Instruction block | The first transcript block that contains the instructions passed to one child. |
| Assignment | The frozen task, supplied context, scope, and requirements for one child. |
| Prepared instructions | Instruction text from the initial request after model selection and prompt binding. |
| Child identity | Session, delegation, assignment, run, and generation identifiers for one child. |

## 4 Non-Scope

Do not change the instructions, model request, tools, permissions, or delegation decisions.
Do not add a second runner, prompt builder, event stream, or transcript store.
Do not ask the model to repeat its instructions in its response.
Do not display private reasoning, credentials, tool schemas, or complete evidence payloads as instructions.
Do not add permanent screen space above the transcript or a new configuration switch.

## 5 Current State

`ChildAgentPrompt.CreateMessages` creates these instruction sections:

- `child-host-policy`.
- `child-role-amendment`.
- `child-repository-instructions`.
- `child-assignment`.

The assignment contains the task and expanded supplied context.
The child loop can add captured evidence references before it builds these messages.
Model selection can bind different prompt text before the initial request starts.
The selected model can also supply `ProviderInstructions`.

`AgentRunLifecycleObserved` carries status and identity. It does not carry the instructions.
`AgentWorkspaceProjection` displays lifecycle details, generated text, and tool activity.
It currently drains generated text before it processes some lifecycle events.

`AgentDisplayStream` can omit fragments when its queue fills.
That stream cannot guarantee delivery of the initial instruction block.

## 6 Proposed Design

### 6.1 Capture the instructions once

1. Use the initial prepared request in `ChildAgentModelLoop` as the source.
2. Capture its instructions after effective model selection and prompt binding.
3. Capture them before the first `StreamAsync` call.
4. Keep section order and text from that request.
5. Include resolved `ProviderInstructions` when present.

Include host policy, role instructions, repository instructions, and the rendered assignment within the display-retention boundary below.
Keep expanded shared context and child-specific context in their delivered order.
Include scope, constraints, output requirements, and evidence references when the delivered instructions contain them.
Do not fetch referenced evidence for this display.

Do not rebuild the display from a task label or from current prompt files.
Do not summarize, paraphrase, or silently shorten the source text.
Keep the model-facing instruction text unchanged.

### 6.2 Use reliable event delivery

Add one host-owned instruction observation to the existing domain event stream.
Use a typed event such as `AgentRunInstructionsObserved`.
Carry the complete child identity, instruction sections, and the source state.
Use source states that distinguish prepared instructions from assignment-only instructions.

Publish a sanitized display copy before the child can produce response text or invoke tools.
Keep the observation outside the model conversation and token accounting.
Do not send it through the lossy generated-text queue.
Do not create another persistence service.

Keep this sequence:

`accepted child -> instruction observation -> child output -> terminal lifecycle`

Domain event publication does not prove that rendering has finished.
Enforce display order inside the projection as well.

### 6.3 Keep the block first

1. Accept instruction observations only for an accepted child identity and generation.
2. Create the child view through the existing lifecycle path.
3. Insert the instruction block before transcript status, response text, diagnostics, and tool output.
4. Drain pending generated text only after the block is present.
5. Record completion of this step in the existing child projection state.

Do not let an instruction event create an unknown child or revive a retired child.
Do not use `RunId` alone when a full child identity is available.
Ignore duplicate observations for the same initial request.

The existing projection gate must serialize instruction insertion and output updates.
If generated text arrives first, retain it under the existing bounded pending-output rules until the block is present.
Keep existing omission notices if those rules discard generated text.

### 6.4 Handle failure before request preparation

Some children fail or stop before an initial model request exists.

1. Use the frozen assignment as the instruction source for these children.
2. Include its task, expanded context, scope, and requirements.
3. Label the source as `Assignment instructions`.
4. State that model instructions were not prepared.
5. Insert this block before the failure or cancellation details.

Use the existing runner boundary for this fallback.
Do not prepare a model request solely to obtain display text.
Do not invent instructions for a child that never received them.

A queued child can keep its status in the existing window header until transcript content becomes available.
If cancellation ends that child, show its assignment block before terminal output.

### 6.5 Render through the existing transcript

Use the heading `Instructions` for prepared instructions.
Use clear section labels for the assignment, role, repository, host, and provider instructions.
Show the text directly when the window first displays its transcript.
Use normal scrolling, selection, and copy behavior.

Render instruction content as quoted text through existing presentation primitives.
Do not interpret instruction text as terminal controls, output actions, or executable links.
Preserve line breaks and task order after required sanitization and terminal encoding.

Keep existing transcript and aggregate child retention limits. Retain stopped-child tabs/transcripts in the existing view owner so selected and unselected early-ending children remain inspectable. Bound stopped delegation generations by the existing retained-delegation limit, keep reused names distinguishable with stable display-only ordinals, forward authoritative terminal status corrections without reopening output, release oldest stopped views with a notice, and return selection to MAIN if its stopped view is evicted. This retirement change was explicitly chosen by the user during implementation.
Do not apply the short lifecycle-reason or progress-label limits to instruction content. Instruction observations instead retain at most 65,536 UTF-16 characters across labels and bodies and at most eight sections per child. Sanitize before taking a prefix, do not split Unicode surrogate pairs, and set `IsTruncated` when content is omitted. Display an explicit retention notice before child output. This bounds durable events and rendering copies without changing model input.
Show `[stopped]` and a small `x` on each retained finished tab.
Keep MAIN first. Insert each new child immediately after MAIN, moving older tabs to the right.
Preserve selection when a new child appears.
Use creation order for eviction so the oldest stopped views leave first.
Close that view when the user clicks `x`.
Keep active children and MAIN without a close control.
Return selection to MAIN when the user closes the selected stopped tab.
Preserve MAIN's draft and reject late output for closed views.
When the user submits a new turn, close stopped views for completed, cancelled, and discarded children.
Keep failed views and queued or running children open.
Use the existing view-removal and selection paths. Keep results and session history.
Do not clear views for local commands, active-run steering, or automatic intelligence refresh.
Use existing explicit omission notices if retention removes content.
Do not replace the instruction block with a summary to meet a label limit.

Keep the block with its child during tab changes and resizing.
Do not duplicate it during provider retries, tool rounds, status updates, or tab reconstruction.
A replacement generation receives its own block.

## 7 Public Contracts

Add only host-owned data to the instruction observation.
Do not expose model-provider SDK or terminal-library types.
Register the event with the existing event serialization catalog.
Use the existing event persistence and redaction path.

Older sessions without the observation must remain readable.
Do not reconstruct historical instructions from current configuration.
Keep headless execution behavior and model requests unchanged.
Headless consumers can receive the same event without creating output windows.

## 8 Project/File Changes

| Area | Files to inspect and change where necessary |
|---|---|
| Event contract | `src/Threadsmith.Core/Events.cs` and its existing serialization registrations. |
| Prepared instruction capture | `src/Threadsmith.Execution/ChildAgentModelLoop.cs`. |
| Early failure handling | `src/Threadsmith.Execution/DelegationOrchestration.cs` and the existing assignment runner. |
| Child output ordering | `src/Threadsmith.Interaction/Agents/AgentWorkspaceProjection.cs`. |
| Rendering | Existing presentation types and `src/Threadsmith.Tui.TuiKit/TranscriptView.cs`, only if current primitives need extension. |
| Verification | Existing delegation tests, `AgentWorkspaceTests.cs`, and event round-trip tests. |
| Documentation | Agent workspace operations, acceptance scenarios, and this plan. |

Reuse `ChildAgentPrompt` output. Do not add a display-only prompt template or alter deployed prompt assets.

## 9 Ordered Tasks

1. Trace all child runner entry points and their terminal failure paths.
2. Confirm the final initial instruction source after model selection.
3. Add the typed observation and serialization registration.
4. Add prepared instruction capture and the assignment-only fallback.
5. Add instruction insertion and output ordering to the existing projection.
6. Render the block through existing transcript primitives.
7. Extend focused tests for content, ordering, isolation, and lifecycle behavior.
8. Update Scenario AU and the agent workspace verification procedure.
9. Run final validation and record the results in this document.

## 10 Testing

Extend existing tests for distinct behavior. Do not add a test-count target or source-text assertions.

| Behavior | Required evidence |
|---|---|
| Exact source | Display sections match the effective initial request after sanitization. Shared context appears once. |
| First position | Instructions precede immediately available response text, tool activity, diagnostics, and terminal output. |
| Isolation | Concurrent children receive only their own instructions. MAIN receives no child instruction block. |
| Lifecycle | Retries and repeated events add no duplicate. New generations receive a new block. Stale events are rejected. |
| Early failure | Failure and cancellation before model preparation show assignment instructions with the correct source label. |
| Safe display | Secrets and terminal controls remain safe. Multiline text, wide characters, resizing, and retention notices remain correct. |
| Compatibility | Event serialization round-trips. Older sessions remain readable. Display capture does not alter model requests or usage. |

Use explicit signals to make output available before the projection handles its instruction observation.
Assert against presentation state only after the projection finishes the relevant operation.
Do not treat a queue acknowledgement as proof of rendering.
Do not use sleeps, arbitrary deadlines, or retries as ordering evidence.

Run the affected delegation, workspace, serialization, and architecture tests.
Run final build and test commands from `CONTRIBUTING.md` for the implementation change.
Run `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` and `git diff --check`.
Record exact commands, outcomes, and platform limits.
Record physical terminal verification separately from automated results.

## 11 Security/Permissions

Sanitize the display copy before event publication and persistence.
Use existing terminal encoding before rendering.
Keep instruction text as data. It must not change tools, permissions, policy, or child authority.
Do not read credentials or referenced artifacts to enrich the display.
Do not expose parent or sibling transcripts.

## 12 Observability

Use the existing event stream and child identity for correlation.
Keep the instruction block separate from assistant output, tool results, and public reasoning.
Do not count displayed instructions as generated model tokens.
Keep source labels accurate when only assignment instructions are available.
Keep existing notices for display omission and retention.

## 13 Migration/Compatibility

No database table migration is required for an additive event type.
Preserve existing event names and payloads.
Do not require historical instruction observations when restoring older sessions.
Do not change prompt files, request ordering, or provider transport formats.

## 14 Acceptance Criteria

1. Every child output window starts with the instructions available for that child.
2. Prepared instruction text comes from the effective initial request.
3. Shared context, child context, and assignment requirements retain their delivered text and order within the display boundary; larger copies show an explicit omission notice, and model input remains complete.
4. Children without prepared requests show assignment instructions and an explicit source explanation.
5. No response, tool output, or terminal detail appears before the instruction block.
6. Retries, tab changes, and repeated events do not duplicate the block.
7. Child identity and generation checks prevent cross-child output and stale updates.
8. Existing sanitization, retention, cancellation, ownership, and headless behavior remain effective.
9. Focused tests and final implementation checks pass.

## 15 Risks

| Risk | Required control |
|---|---|
| A task label omits real instructions. | Capture the prepared request sections. |
| Model rebinding changes prompt text. | Capture after effective model selection. |
| Generated text arrives before the event is processed. | Gate transcript output on instruction insertion. |
| A lossy queue discards instructions. | Use the existing reliable domain event stream. |
| Long instructions consume retained output. | Reuse retention limits and explicit omission notices. |
| A child stops before request preparation. | Show its frozen assignment with an accurate source label. |
| Duplicate or stale events corrupt a window. | Use complete identity checks and one initial block per child. |

## 16 Documentation

Add this plan and one navigation row now.
During implementation, update Scenario AU and [the agent workspace guide](../operations/agent-workspace.md).
Add manual steps for initial instructions, concurrent children, early failure, and text selection.
Keep milestone status and completed milestone contracts unchanged.
Keep implementation status and validation evidence in this document.

## 17 Open Decisions

No product decision blocks implementation. Instruction observations use host-owned sections and the existing domain event stream. Existing literal transcript segments provide quoted rendering. Stopped children remain in the existing bounded view owner, as selected by the user during implementation.

## 18 Implementation verification

### P2 review repairs — current validation

The queued checkpoint again forwards caller cancellation. Terminal retention uses the existing metadata projection before removing request status; later corrections preserve that final snapshot. Instruction observations have an explicit display-only character/section boundary and omission flag. Duplicate capture is suppressed before section construction. Sixteen deterministic regression cases cover stalled queued publication, final metadata without a refresh, terminal corrections, concurrent oversized observations, serialized size, exact boundaries, Unicode, redaction, and omission ordering.

Final Windows checks for these repairs:

- `dotnet restore src/Threadsmith.sln`: passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` through the native build tool: passed, no diagnostics or omissions reported.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,289 passed, 71 skipped, one failed. CoreRuntime, ParallelAgents, Architecture, and persistence tests passed. The sole failure is the unchanged `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation` readiness assertion at line 1213, before cancellation is requested; this same failure was recorded below before these repairs. It remains outside this task's scope.
- Scoped whitespace verification for the eight changed C# files: passed.
- Completed background compiler/analyzer feedback for those eight documents: no errors, warnings, or new task-related suggestions. The existing RCS1261 suggestion on the cancellation registration in `DelegationOrchestration.cs` remains. This is document-scoped evidence, not a clean whole-solution informational-analyzer claim.
- Packaged-documentation preflight and `git diff --check`: passed.

A focused production-path adversarial review found no concrete repair defects. No physical-terminal, macOS/Linux, live-provider, allocation, database-growth, or restoration benchmark is claimed. The boundary bounds retained event/rendering copies; materializing and sanitizing the original source section still costs work proportional to its input size. The earlier verification entries below are historical results, not fresh evidence for these repairs.

Verified in the active checkout `C:/source/repos/Threadsmith` on Windows with .NET SDK `10.0.403`.

### Adversarial review

Clean-context review covered security, correctness, architecture, performance, and tests. Valid findings were repaired: XML-escaped credential redaction, early-ending child inspectability, reused-name labels, authoritative terminal status corrections, and cached tab-label invalidation. The follow-up review found no supported remaining security, correctness, or architecture defect. A separate focused test review closed the initially incomplete assessment of both new test files and found no actionable test defect or critical coverage gap.

The proposed removal of repeated sanitization was not accepted. The XML-aware pass handles escaped text nodes; the final whole-section pass also covers raw text, tag fragments, provider content, assignment-only content, and labels. A focused independent review confirmed that removing it would weaken coverage or require extra parsing complexity. Its performance cost is unmeasured and remains an observation, not a supported blocker. No alternate event pipeline or extra test machinery was added to satisfy that recommendation.

### Final commands and outcomes

- `git rev-parse --show-toplevel`: confirmed `C:/source/repos/Threadsmith`.
- `dotnet restore src/Threadsmith.sln`: passed; all projects up to date.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed through the native build tool, with no reported diagnostics or omitted diagnostics.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: **failed**, with 5,277 passed, 71 skipped, and 1 failed out of 5,349. CoreRuntime (workspace and event serialization), ParallelAgents (instruction capture and fallback), and Architecture test projects passed.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`: passed; 118 packaged documents validated.
- `git diff --check`: passed.

Changed-file whitespace check (passed):

```powershell
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.App/ApplicationComposition.cs src/Threadsmith.Core/Events.cs src/Threadsmith.Execution/ChildAgentInstructionObservation.cs src/Threadsmith.Execution/ChildAgentModelLoop.cs src/Threadsmith.Execution/DelegationOrchestration.cs src/Threadsmith.Interaction/Agents/AgentWorkspaceProjection.cs src/Threadsmith.Tui.TuiKit/AgentTabStrip.cs src/Threadsmith.Tui.TuiKit/AgentViews.cs tests/Threadsmith.CoreRuntime.Tests/AgentWorkspaceTests.cs tests/Threadsmith.CoreRuntime.Tests/AgentWorkspaceTests.Instructions.cs tests/Threadsmith.CoreRuntime.Tests/Milestone1Tests.cs tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.Instructions.cs
```

Changed-file analyzer check, including informational severity:

```powershell
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.App/ApplicationComposition.cs src/Threadsmith.Core/Events.cs src/Threadsmith.Execution/ChildAgentInstructionObservation.cs src/Threadsmith.Execution/ChildAgentModelLoop.cs src/Threadsmith.Execution/DelegationOrchestration.cs src/Threadsmith.Interaction/Agents/AgentWorkspaceProjection.cs src/Threadsmith.Tui.TuiKit/AgentTabStrip.cs src/Threadsmith.Tui.TuiKit/AgentViews.cs tests/Threadsmith.CoreRuntime.Tests/AgentWorkspaceTests.cs tests/Threadsmith.CoreRuntime.Tests/AgentWorkspaceTests.Instructions.cs tests/Threadsmith.CoreRuntime.Tests/Milestone1Tests.cs tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.Instructions.cs
```

This check exited 1 with nine informational findings on unchanged code: CA1826 in `ChildAgentModelLoop`, RCS1261 on the existing cancellation registration in `DelegationOrchestration`, and seven suggestions in `Milestone1Tests` (CA1865, CA1861, two RCS1118, RCS1205, RCS1214, and xUnit2032). The diff and HEAD source confirm that these statements were not introduced by this change. No task-related analyzer repair remains; this is not a clean informational-analyzer result for those entire files.

### Outstanding validation limits

The single failing test is `Threadsmith.Validation.Tests.Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation`, at `Milestone6Tests.cs:1213`. The build task completes before the delayed child publishes `child.pid`, so the readiness assertion fails before the test requests cancellation. The test, its fixture, and `BuildExecutor` are unchanged and do not exercise instruction display. The underlying readiness failure remains unresolved and out of this implementation's scope. It was not hidden by filtering, increasing deadlines, or retrying until green. Acceptance criterion 9 is therefore not fully satisfied for the whole solution.

No physical-terminal painting, interactive selection/copy, Linux/macOS, live-provider, or performance verification is claimed. The maintained manual procedure covers these remaining checks. Automated literal rendering, Unicode/resizing, ordering, identity isolation, assignment fallback, serialization, stopped-view retention, eviction, and late-output rejection are covered by the passing affected projects. No commit or push was performed.


### Finished-tab state and close control

Finished tabs now show `[stopped]` and a small `x` in their actual compact labels.
The label retains the close control when names need abbreviation.
F2 details use the full retained-view label.

The click uses the existing measured tab cells and view owner.
MAIN and active children have no close control.
Closing the selected finished view returns to MAIN through the existing selection path.
Closing another finished view preserves selection.
MAIN's draft remains intact.
Late terminal corrections cannot recreate a closed view.
Automatic eviction and manual closure share the same view-removal method.

Final Windows validation for this addition:

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed with zero warnings and errors.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class '*AgentWorkspaceTests'`: 49 passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,272 passed, 71 skipped, zero failed.
- Changed-file whitespace verification: passed.
- Changed-file analyzer verification: three existing RCS1146 suggestions on unchanged lines in `TuiKitSurface.cs`; no diagnostics on changed lines.
- Packaged-documentation preflight and `git diff --check`: passed.

The rendered mouse-routing test observes a completed frame before clicking the close cell.
It verifies return to MAIN and exact preservation of a multiline draft.
Other focused tests cover visible Unicode hit cells, stale clicks, terminal corrections, label widths, and live-view protection.
No sleeps or test deadlines control their outcomes.
Use MTP-AGENT-TABS for physical terminal verification.
Physical Windows and Unix mouse checks remain unverified.
Detailed commands and logs are recorded in `.inbox/stopped-tab-close-*`.


### Newest-first child tabs

MAIN remains first. New child views enter immediately after MAIN.
Older views move toward right-side overflow without changing selection.
Eviction uses creation ordinals independently of display order.
Overflow arrows keep their painted view targets when a new child arrives before a click.

Existing workspace tests cover newest-first insertion, preserved selection, Unicode click targets, oldest-first eviction, and overflow navigation.
A controlled render-insert-click sequence verifies the overflow arrow target.
No new test cases were added for this change.

Final Windows validation:

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed with zero warnings and errors.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class '*AgentWorkspaceTests'`: 49 passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,272 passed, 71 skipped, zero failed.
- Scoped analyzer and whitespace checks: passed with no diagnostics.
- Packaged-documentation preflight and `git diff --check`: passed.

Exact commands and logs are in `.inbox/newest-agent-tabs-*`.
Physical Windows and Unix terminal checks remain unverified.

### Clear stopped nonfailed tabs on a new user turn

The interaction coordinator requests cleanup before it submits a new user turn.
Automatic intelligence refresh does not request cleanup.
Local commands and active-run steering use their existing paths.
The surface queues cleanup on its existing owner loop.
The view owner checks the session and removes completed, cancelled, and discarded views.
It retains MAIN, failed views, and queued or running children.
Cleanup uses the existing removal method and selection-change path.
It does not change child execution, stored results, or session history.

The state test covers all child statuses, stale sessions, selected-view closure, idempotence, and late terminal updates.
The existing rendered close test also covers new-turn cleanup and preservation of MAIN's multiline draft.
The coordinator test confirms one cleanup request for a user turn and none for `/quit`.
No sleeps or wall-clock test deadlines were added.

Final Windows validation:

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed with zero warnings and errors.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,274 passed, 71 skipped, zero failed.
- Scoped whitespace verification: passed.
- Scoped analyzer verification at informational severity: seven existing suggestions on unchanged lines in `Milestone1Tests.cs`; no findings on changed lines.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`: passed; 118 documents validated.
- `git diff --check`: passed.

Commands and logs are in `.inbox/new-turn-agent-cleanup-*`.
Physical-terminal and macOS/Linux checks remain unverified.
