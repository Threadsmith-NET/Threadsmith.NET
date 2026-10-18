# Clipboard attachment progress

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Completed clipboard image admission and composer attachment output.

## 1 Objective

Explain clipboard capture/import latency immediately and prevent duplicate concurrent image pastes.

## 2 Architectural Context

Use the existing TUIKit clipboard task, UI update queue and main transcript presentation.

## 3 Scope

Show "Attaching image ..." before capture starts; verify repeated paste suppression and recovery through capture and artifact admission.

## 4 Non-Scope

New clipboard formats, PDF clipboard support, changes to text paste or model execution.

## 5 Current State

The existing clipboard task already excludes repeat requests until completion, including failure. Import progress was not visible before completion.

## 6 Proposed Design

Queue a status/transcript entry before reading the clipboard, using the same presentation path as successful attachment. Retain the task-completion gate shared by the shortcut and attachment menu. Do not add another busy flag or queue duplicate requests.

## 7 Public Contracts

The output displays "Attaching image ..." while attachment work runs. Additional image paste attempts are ignored until completion; success and handled failure allow another paste.

## 8 Project/File Changes

TuiKitSurface, TuiKitImageInputTests, user guide, Scenario BB and MTP-279.

## 9 Ordered Tasks

Add progress, exercise controlled capture/storage delays and recovery, build and review the actual input path.

## 10 Testing

Headless terminal tests use barriers during capture and storage, feed repeated shortcut events followed by ordinary text, and check that only one capture occurs. Retry after success, capture failure, timeout, empty clipboard and store failure. Existing shortcut, menu, text paste, draft preservation and secondary-prompt tests remain applicable.

## 11 Security/Permissions

No change to clipboard authority, artifact ownership or session/epoch validation. Existing cancellation and unretained artifact cleanup remain in place.

## 12 Observability

Progress appears in the main output and activity notice before capture. Existing success/error messages remain the outcome indication. No disabled-state message is added.

## 13 Migration/Compatibility

No migration or configuration change. Ctrl+V remains text paste; Ctrl+Alt+V and the F9 menu retain their image path.

## 14 Acceptance Criteria

Scenario BB and MTP-279 cover visible progress, ignored concurrent requests and recovery.

## 15 Risks

Headless terminal tests exercise the real input and rendering loop with controlled clipboard/storage adapters; they do not establish native clipboard behavior on every platform.

## 16 Documentation

Update the existing user workflow and acceptance/manual procedure; preserve completed milestone contracts.

## 17 Open Decisions

None.

## Verification record (2026-10-18)

- Solution build passed with zero warnings/errors. Clipboard/composer suite: 23 passed, two opt-in native clipboard checks skipped. Barrier-based regressions cover repeated requests during capture and storage, followed by retry after success, capture failure, timeout, empty clipboard and admission failure.
- Fresh-context adversarial review found no material issues in the scoped change. Existing task ownership handles exclusion and recovery without a second busy state. Source documentation, planning hygiene and diff whitespace validation passed.
- Tests used the real terminal input/rendering loop with controlled clipboard and storage adapters on Windows. Native clipboard behavior and other platforms were not revalidated. No model call is involved in this frontend behavior.
