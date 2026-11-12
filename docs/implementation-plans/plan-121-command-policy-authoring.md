# Guided command policy authoring

**Status:** Planned. Implementation has not started.
**Delivery track:** M37 — Guided command policy authoring.
**Prerequisites:** Existing M3, M7.1, M7.2, and M20 capability contracts; ADR-63; the implemented shell policy and retained modal paths.
**Date:** 2026-11-12.
**Writing standard:** ASD-STE100 Simplified Technical English. Identifiers, library names, and policy terms are technical names.

**Acceptance contract:** [Scenario COMMAND-POLICY-AUTHOR](acceptance-scenarios.md#scenario-command-policy-author--guided-command-policy-authoring).
**Manual procedure:** [MTP-COMMAND-POLICY-AUTHOR](manual-test-plan.md#mtp-command-policy-author--guided-command-policy-authoring).
**Milestone contract:** [M37](milestones/milestone-37-guided-command-policy-authoring.md).

## 1 Objective

Open a guided modal when the user enters `/command-policy` without arguments.
Ask one question at a time.
Accept several shell command examples and the required policy outcome for each example.
Generate JSON for manual use in machine or user configuration.

Show the scope of each proposed rule before the user copies the JSON.
Use the existing policy implementation to check the proposal.
Keep the commands, policy details, and generated JSON on the local user surface.

## 2 Architectural Context

Read these contracts before implementation:

- Root `AGENTS.md` and `docs/guardrails/portable-csharp-guardrails.md`.
- [Planning governance](planning-governance.md) and [shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).
- [ADR-5](../architecture/adr-05-ui-as-projection-command-adapter.md): UI projection and command adapters.
- [ADR-11](../architecture/adr-11-central-tool-policy-pipeline.md): central tool policy.
- [ADR-62](../architecture/adr-62-sole-tuikit-interactive-frontend.md): the sole TUIKit frontend.
- [ADR-63](../architecture/adr-63-shared-command-policy.md): shared command policy and immutable shell binding.
- [Command policy reference](../operations/command-policy.md).
- Existing acceptance scenarios SC106-01 through SC106-07.

Keep policy rules and compilation in `Threadsmith.Policies`.
Keep shell preparation in `Threadsmith.Tools`.
Keep interaction contracts free of TUIKit types.
Use the existing retained modal owner and terminal event loop.

Before adding a type, find the existing owner of that responsibility.
Trace its callers.
Extend that owner when possible.
Record a technical reason for each separate path.
A shared formatter alone does not prove shared execution.

## 3 Scope

- Open the modal from `/command-policy` without arguments.
- Preserve the existing shell-specific management commands.
- Accept one selected shell and several command examples in each draft.
- Support the five existing policy outcomes.
- Select a working-directory scope and a machine or user destination.
- Suggest suitable extensions to existing ordinary rules.
- Generate complete rule objects, executable-list additions, and merge instructions.
- Check syntax, policy conflicts, and the expected outcome for each example.
- Provide review, previous-step navigation, cancellation, and explicit clipboard copy.

## 4 Non-Scope

- Model calls, maintained skills, model-facing tools, or skill-specific host branches.
- Execution of command examples, script bodies, discovery probes, or shell startup hooks.
- Configuration writes, automatic activation, elevation, or persistent approval grants.
- New matchers, positive argument grammars, or changes to execution policy.
- Automatic construction of argument-deny expressions from examples.
- Creation or removal of machine-enforced constraints.
- Policy authoring for a different operating system from the current host.
- A separate modal runtime, command dispatcher, terminal editor, or shell parser.

## 5 Current State

These observations apply to the current checkout.
Check them again before implementation.

| Area | Existing owner | Required use |
|---|---|---|
| Slash command | `InteractiveCommandCatalog` and `InteractionCoordinator.CommandPolicy.cs` | Extend the existing route. No arguments currently show usage text. |
| Management | `CommandPolicyCoordinator` | Preserve inspect, reload, revoke, and reset behavior. |
| Shell facts | `ShellCommandPreparation` | Reuse static parsing, native binding, and interpreter/script actions. |
| JSON | `PolicyJson` | Reuse strict parsing, serialization, and document limits. |
| Rule checking | `PolicyCompiler` and `PolicyEvaluator` | Compile the candidate layers and evaluate every prepared action. |
| Trusted sources | `SecurityPolicyConfigurationLoader` and repository binding | Obtain one consistent machine/user source revision through these owners. |
| Modal lifecycle | `TuiKitSurface.Retained.cs` | Reuse `RunRetainedActionModalAsync`, modal gates, queue dispatch, and cancellation cleanup. |
| Question navigation | `QuestionModal` | Reuse the established focus, choice, details, and previous/next behavior where applicable. |
| Text entry | `RepositoryMemoryEditModal` and `TuiKitComposer` | Reuse the editor, paste handling, selection, undo, and input limits. |
| Choice presentation | `ChoiceModal` | Reuse choice identity, selection, filter, and detail patterns. |
| Review table | `ToolStatisticsModal` | Reuse TUIKit `DataTable<T>`, `DataColumn<T>`, and `ScrollView`. |
| Frame and styles | `ModalFrame`, `WorkspaceLayout`, and semantic text roles | Use the same geometry, theme, and minimum terminal size. |
| Clipboard | `TuiKitSurface` copy and paste callbacks | Preserve explicit copy, size limits, and stale-result checks. |

`QuestionModal` also serves model clarification requests.
Do not route this feature through `ask_user`, its enablement setting, or its suspension lifecycle.
Reuse the presentation behavior without adding another question service.

## 6 Proposed Design

### 6.1 Entry and draft ownership

Handle the empty argument before the existing shell argument parsing.
Open one retained modal through the existing interaction owner.
Keep one bounded draft for its lifetime.
Keep the conversation composer text unchanged.

Open the modal before requiring an active shell profile.
Show unavailable or invalid policy sources as local errors.
Do not substitute a permissive policy when source loading fails.

Use the current host platform.
Offer shells supported by the existing preparation path on that platform.
Show missing shells as unavailable.
Use file lookup and existing binding checks; do not run a shell to discover capabilities.

### 6.2 Sequential questions

| Step | Question or action | Required behavior |
|---|---|---|
| 1 | Which shell will run these commands? | Show the configured shell and other supported choices. Require confirmation. |
| 2 | Which commands need rules? | Accept one complete command per row. Support bulk paste with one command per line. |
| 3 | What control is required? | Ask for each command. Offer the five existing outcomes. Require an explicit choice. |
| 4 | Where can these rules apply? | Select the current working directory, another absolute directory, or all working directories. Show descendant scope. |
| 5 | Where will the JSON be added? | Select user or machine configuration. Show the resolved trusted configuration path. |
| 6 | Are these rules correct? | Show changes, coverage, validation, JSON, and merge instructions. Offer Back, Copy JSON, and Close. |

Use these outcome values:

| Display choice | JSON value |
|---|---|
| Allow | `Allow` |
| Deny | `AlwaysDeny` |
| Ask; permit a saved choice | `ApprovalRequired` |
| Ask; permit a session choice | `ApprovalRequiredSession` |
| Ask every time | `ApprovalAlwaysRequired` |

Use the current approval descriptions from the policy reference.
Do not imply that a rule bypasses other host controls.
Initial focus is not an answer.

Retain answers when the user moves to an earlier step.
After an edit, invalidate affected command facts and the complete generated preview.
Keep source text unchanged until the user edits it.
Ignore empty rows during bulk paste.
Reject unsupported multiline commands instead of joining their lines.
Do not split command rows at quoted separators.

### 6.3 Shared modal components and widgets

Compose the flow from the existing modal components and TUIKit widgets.
Reuse `ModalFrame`, `WorkspaceLayout`, `TuiKitInput.Normalize`, `TuiKitComposer`, and `TranscriptView`.
Use the existing TUIKit `Modal` base and retained surface lifecycle.
Use `DataTable<T>` with `ScrollView` for the command/result table.
Use the existing safe text renderer for labels and JSON details.

Reuse the focus and navigation behavior from current question and edit modals.
If shared code needs extraction, keep it small and use it from both callers.
Do not copy an existing editor, list-navigation engine, or modal input loop.
Do not introduce a general wizard framework.

Keep all mutable widget state on the terminal owner thread.
Deliver host validation results through the existing queue.
Permit only one host action at a time.
Ignore a late validation result after its draft revision changes or its modal closes.

### 6.4 Keyboard and pointer controls

Use the same controls as the corresponding existing modal component.
Do not add a new shortcut scheme.

| Context | Control | Behavior |
|---|---|---|
| Modal | `Esc` | Cancel and close. Release input ownership. |
| Modal | `Ctrl+C` | Use the existing interrupt action. Do not assign copy to this key. |
| Fields and actions | `Tab` / `Shift+Tab` | Move focus forward or backward. |
| Choice list | `Up` / `Down` | Move through choices. Focus alone does not confirm an answer. |
| Choice list | `Space` | Select the focused choice, as in the question modal. |
| Focused action | `Enter` | Activate Next, Back, Copy JSON, or Close. |
| Command editor | Existing editor controls | Preserve text selection, cursor movement, undo, and newline insertion. |
| Text entry | `Ctrl+V` / `Shift+Insert` | Use the shared paste callback. |
| Question navigation | `PgUp` / `PgDn` | Use the existing previous/next question behavior outside text and preview scrolling. |
| Details or review | Existing scroll controls | Preserve arrow, page, Home, and End behavior of the focused component. |
| Details | `F2` | Show or hide details. |
| Preview selection | `F6` | Use the existing detail-copy behavior for selected text. |
| Modal | `F12` | Use the shared terminal mouse-capture toggle. |

Keep a visible Back action; navigation must not depend on a function key.
Use the explicit Copy JSON action to copy the complete generated fragment.
Show only controls that apply to the current page and focus.
Do not assign reload, reset, save, or approval actions to this authoring modal.

Reuse pointer translation and wheel behavior from the existing components.
Use the same semantic styles and selection markers.
Preserve the draft during terminal resize.
Below 40 columns or 12 rows, use the existing minimum-size behavior.
Suspend editing and copying until the frame fits.
Keep cancellation and input cleanup available.

### 6.5 Command analysis and rule generation

Prepare commands through the same path used by `run_process`.
Separate preparation from launch at the existing boundary.
Do not add another tokenizer or command-name interpretation table.
Preserve literal argument values, aliases, command kinds, and resolved targets.

Show each interpreter and script action separately.
Show each action in an admitted sequence or pipeline.
The requested outcome applies to the complete command.
Use the existing combined-action outcome rules.
For scripts, permit a shared interpreter rule while the script rule supplies the requested control.
Do not force the interpreter and script to have identical outcomes.

Require compatible requested outcomes for actions shared by several examples.
Report unresolved conflicts instead of choosing an outcome from row order.

Generate ordinary rules in the selected platform/shell profile.
Use executable identity, command kind, and the selected directory scope.
Use an exact script path for a script example.
Do not replace an absolute target with a basename to make validation succeed.
Show basename scope explicitly when the user selects a name-based rule.

First check whether an existing rule already supplies the requested behavior.
When possible, propose an extension in the selected destination.
Preserve its ID, outcome, directory limits, deny expressions, and override relationships.
Preserve the meaning of each existing matcher branch.
Show the complete replacement rule and its destination.
Otherwise, generate a new rule with a stable, collision-checked ID.

Do not change an unrelated rule to remove a conflict.
For an ordinary rule conflict, show the affected rule and the proposed scope.
Ask the user to confirm a scoped override or revise the command row.
Generate an explicit `overrides` entry only after that choice.

Validate its targets and graph through the existing compiler.
Do not disable inherited rules or change machine-enforced constraints.
Keep argument-deny expressions effective under the existing override semantics.

Show argument coverage beside each example.
Existing ordinary matchers do not positively select a complete argument list.
An executable rule can therefore cover other subcommands and options.
Do not describe generated rules as exact-command grants.
If different controls require unsupported argument distinctions, report that limit.

Directory scope checks the working directory and its descendants.
It does not confine file arguments or downstream program behavior.
Script authorization approves a path and invocation, not script contents.

### 6.6 Candidate validation and export

Read trusted source documents through the existing configuration owner.
Capture their revision and effective tool/executable restrictions.
Create the candidate layers in memory.
Merge rule objects by ID using the existing policy semantics.
Use `PolicyJson`, `PolicyCompiler`, and `PolicyEvaluator` for checks.

Check every prepared action, including interpreter and script actions.
Check the outer shell against the executable list.
Check interpreters, executables, scripts, and literal cmdlet/alias names through command policy.
Do not require duplicate executable-list entries for commands inside the shell.
Offer command-name groups such as `Select-*`.
Include literal pipelines, comma arrays, and redirection in command examples.
Show machine-enforced restrictions and unresolved policy conflicts on the user surface.
Do not generate saved `alwaysAllow` entries.

Show requested and candidate outcomes in the review table.
For a requested Deny, require an explicit matching denial rule.
Default-deny, unsupported syntax, or preparation failure does not satisfy that request.
Report static policy eligibility separately from runtime permission.
Tool enablement, trust, hooks, mandatory approval, and other execution controls still apply.
Do not claim an execution test or a future successful launch.

Disable Copy JSON while parsing, compilation, or requested-outcome checks fail.
Require the user to correct or explicitly remove unsupported command rows.
Show omissions before another validation attempt.
Do not silently omit commands or malformed source profiles.

Copy a valid JSON fragment with the required `tools` hierarchy.
Include `schemaVersion` and the selected platform/shell profile.
For an extended rule, include its complete replacement object with the same ID.
List executable-array additions separately in the merge instructions.
Explain that ordinary arrays replace inherited arrays.
Do not output an executable array that discards existing entries.

Use resolved configuration paths from the host.
Explain the exact merge location and each add or replace operation.
Explain reload after policy-only edits and restart after outer-list or shell changes.
Do not call the publication service or activate the proposal.

Before copy, recheck the source revision and native binding inputs.
If either changed, invalidate the preview and validate again.
The clipboard operation must copy the exact reviewed JSON.
Do not truncate it.
Show clipboard failures and retain the draft.

### 6.7 Bounds and lifetime

Limit one draft to 32 command rows and 64 KiB of command input in UTF-8.
Also enforce existing per-command, policy-document, editor, transcript, and clipboard limits.
Apply the smallest applicable limit before expensive work or copying.
Keep an over-limit paste from changing the previous valid draft.

Prepare and validate only the supplied commands and affected policy sources.
Do not scan repository contents, shell modules, command history, or unrelated configuration data.
Use cancellation through every asynchronous boundary.
Cancel pending work on dismissal, shutdown, session reset, or repository replacement.
Keep source revision checks for changes that occur during the modal.

## 7 Public Contracts

Use host-owned request, draft, action-summary, and preview records.
Keep the selected shell, outcome, directory, destination, and draft revision explicit.
Keep the source revision attached to the preview.
Return structured errors and rule changes; do not parse presentation text for decisions.

Extend the optional retained interaction surface pattern.
Keep policy generation available below the frontend, with an asynchronous host interface.
Do not place widgets or terminal keys in that interface.
Do not expose raw policy decisions as model tool results.

Use a bounded unavailable message when the frontend cannot present this modal.
Do not submit the command to the model as fallback.
Leave headless CLI generation outside this feature.

## 8 Project/File Changes

| Project or file | Planned change |
|---|---|
| `Threadsmith.Policies` | Generate ordinary rule proposals from supplied typed facts. Reuse compilation and matching. Keep this library free of process and frontend references. |
| `Threadsmith.Tools` | Add the read-only authoring operation around existing preparation and policy checks. Keep launch unreachable. |
| `Threadsmith.App` | Bind the authoring operation to the existing trusted configuration source owner and resolved paths. |
| `InteractionCoordinator.CommandPolicy.cs` | Open the modal for empty arguments. Preserve existing operations. |
| `InteractiveCommandCatalog` | Document the no-argument flow and retain management syntax. |
| `Threadsmith.Interaction/Contracts` | Add the optional retained surface contract and bounded projections. |
| `TuiKitSurface.Retained.cs` | Host authoring through the existing modal action loop and clipboard callbacks. |
| `Threadsmith.Tui.TuiKit` | Compose the authoring pages from the shared frame, editor, choices, table, and scroll widgets. |
| Existing policy and interaction test projects | Add behavioral tests at the owning boundaries. |

Confirm final file and type names during implementation.
Do not create duplicate configuration loaders or policy stores.

## 9 Ordered Tasks

1. Trace slash routing, modal ownership, source loading, preparation, compilation, and clipboard callbacks.
2. Define bounded authoring records and the source-revision contract.
3. Add rule generation from typed facts. Keep existing rule meanings intact.
4. Add read-only candidate validation through the existing preparation and policy owners.
5. Add the no-argument route and optional retained surface contract.
6. Compose the sequential modal and connect retained host actions.
7. Add review, explicit copy, revision checks, and merge instructions.
8. Add deterministic behavioral tests and complete the manual procedure.
9. Update user help and command policy documentation.
10. Validate the final change set and report platform limits.

## 10 Testing

Use observable completion and explicit barriers.
Use owner-thread snapshots or close and join the owner before reading mutable widget state.
Do not use sleeps, runner-speed deadlines, or retries until success.
Assert typed outcomes, state, JSON structure, and policy decisions.
Do not add assertions for specific explanatory text.

Cover these cases:

- No-argument routing, existing management operations, and non-modal surface fallback.
- All five outcomes, explicit selection, Back navigation, correction, and cancellation at each step.
- PowerShell native commands, literal cmdlets, aliases, script files, and explicit interpreter paths.
- Supported Bash, sh, zsh, CMD, and Windows PowerShell cases on their actual host platforms.
- Multiple actions, shared interpreter actions with different script outcomes, missing scripts, quoting, and rejected dynamic syntax.
- Existing-rule reuse, safe extension, ID collisions, conflicting outcomes, and explicit scoped overrides of ordinary rules.
- Argument-scope limits, machine constraints, invalid sources, and all outer executable-list requirements.
- JSON escaping, source-layer merging, complete replacement rules, and unrelated setting preservation.
- A recording launch boundary that proves every authoring path performs zero command launches.
- No configuration writes, activation calls, persistent grants, or model requests.
- Source changes, PATH changes, stale results, session changes, and cancellation during validation.
- Shared key controls, focus order, paste fencing, table scrolling, themes, and mouse capture.
- Wide text, combining characters, control characters, terminal resize, and the shared minimum size.
- Input limits, preview limits, clipboard failure, exact copied bytes, and no silent truncation.
- Policy canaries absent from logs, transcript, events, model context, and restored session state.

Run applicable focused tests and the final solution checks from `CONTRIBUTING.md`.
Run the documentation preflight.
Record exact commands and results.
Report each unavailable OS/shell pair separately.
Parser fixtures do not prove real-terminal or real-shell behavior.

## 11 Security/Permissions

Treat command examples as data.
Do not run them, import their scripts, or load their shell profiles.
Keep all active command authorization gates unchanged.

Read policy sources only through existing trusted configuration boundaries.
Do not give the model access to machine/user files or policy details.
Do not weaken machine constraints or invent missing consent.
Do not read unrelated credentials from configuration for presentation.

Use safe text for terminal display.
Use proper JSON serialization for copied values.
Keep command drafts out of composer history and persistent session state.
Clear draft references on modal disposal.

## 12 Observability

Use the existing local modal status and error presentation.
Show pending validation and clipboard completion on that surface.
Avoid a second tool activity or model conversation entry.

If operational metrics are required, record counts, duration, cancellation, and bounded reason codes only.
Do not record commands, paths, rule bodies, generated JSON, or clipboard content.
Do not store the draft in ordinary events or retrieved diagnostics.

## 13 Migration/Compatibility

Change only the no-argument behavior of `/command-policy`.
Keep `/command-policy <shell> [inspect|revoke <id>|reload|reset-session]` compatible.
Update command help and completion to show both forms.

Keep the policy schema unchanged.
Keep current layer precedence, array replacement, and explicit override semantics.
Generated JSON remains a manual proposal until the user changes trusted configuration.

## 14 Acceptance Criteria

- `/command-policy` opens a modal without arguments.
- The modal asks sequential questions and retains answers during navigation.
- The modal uses the existing components, TUIKit widgets, styles, and keyboard controls.
- The generated JSON covers every retained command example with the selected outcome.
- The review shows broader permission scope and all required interpreter/script actions.
- Suitable existing ordinary rules are reused or extended without unrelated changes.
- Invalid or stale proposals cannot be copied as validated configuration.
- The copied JSON and merge instructions preserve unrelated configuration.
- The authoring path launches no supplied commands and writes no configuration.
- Policy content remains restricted to the direct local user surface.
- Existing management operations and execution gates remain unchanged.
- Required checks pass, with platform and terminal limits reported separately.

## 15 Risks

| Risk | Required control |
|---|---|
| A command example implies narrower permission than a rule provides. | Show executable and argument scope. Reject unsupported requested distinctions. |
| An existing rule is broadened unexpectedly. | Preserve existing branches and show the complete proposed replacement. |
| Preview and active configuration diverge. | Bind the preview to source and binding revisions. Recheck before copy. |
| Rule checks omit script or outer-list authority. | Reuse prepared actions and check each required name. |
| A new modal behaves differently. | Reuse shared components and test the existing controls. |
| A preparation change accidentally launches a process. | Keep preparation separate and assert zero launch calls. |
| Commands or policy data reach the model. | Keep authoring outside conversation, tools, and persistent event payloads. |

## 16 Documentation

This plan adds its acceptance scenario, manual procedure, milestone contract, and navigation entries.
Do not edit completed milestone contracts.
Keep current milestone lifecycle status only in `milestones.md`.

During implementation, update `docs/user-guide.md` and `docs/operations/command-policy.md`.
Document the no-argument modal, shared controls, outcomes, merge instructions, and validation limits.
Include examples for native commands, cmdlets, aliases, and script/interpreter pairs.
Explain executable-array replacement and reload versus restart.
Do not present this feature as available before implementation.

## 17 Open Decisions

There are no open product decisions for this plan.
Choose final internal type names during implementation.
Keep those choices within the stated ownership and reuse requirements.
Record any proposed scope change before implementation.
