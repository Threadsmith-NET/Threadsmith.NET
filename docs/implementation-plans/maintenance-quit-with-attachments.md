# Quit with pending image attachments

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Clipboard image admission and recoverable composer submissions.

## 1 Objective

Allow `/quit` with pending or preserved failed image attachments.

## 2 Architectural Context

Shared command routing owns exit. The frontend owns unsubmitted images until normal disposal releases their references.

## 3 Scope

Recognize quit before attachment rejection; preserve frontend ownership for cleanup and bypass the failed-draft submission guard when quitting.

## 4 Non-Scope

Allowing other slash commands with images, changing conversation persistence, or introducing new shutdown machinery.

## 5 Current State

Attachment rejection precedes quit routing, requiring manual removal before exit. Composer submission normally transfers image ownership away from its draft.

## 6 Proposed Design

Move exact trimmed case-insensitive quit handling before the shared slash/image guard. The composer recognizes the same exit text only to permit submission and retain draft ownership for its existing disposal path. The shared coordinator still decides whether to exit.

## 7 Public Contracts

Quit exits without attachment removal or a model request. Other slash commands retain existing restrictions.

## 8 Project/File Changes

InteractionCoordinator, TuiKitSurface, coordinator and composer regressions, user guide, Scenario BB and MTP-279.

## 9 Ordered Tasks

Adjust routing and ownership, verify quit and unchanged command guards, build and review.

## 10 Testing

Coordinator tests cover quit casing/whitespace with attachments, no model work, and continued rejection of other commands. Real input-loop tests cover pasted images, preserved failed submissions and release during disposal.

## 11 Security/Permissions

Reuse reference-aware image release during disposal. No new file, model or command authority.

## 12 Observability

Quit no longer displays the attachment-removal error. No new output or confirmation.

## 13 Migration/Compatibility

No schema or configuration changes.

## 14 Acceptance Criteria

Scenario BB and MTP-279 cover quitting with pending attachments and ordinary shutdown cleanup.

## 15 Risks

Frontend ownership must remain intact until disposal; merely moving command routing would leave submitted image references unowned by the composer.

## 16 Documentation

Update the current user workflow and acceptance/manual procedure without changing completed milestone contracts.

## 17 Open Decisions

None.

## Verification record (2026-10-18)

- Solution build passed with zero warnings/errors. Coordinator regressions: 62 passed. Clipboard/composer regressions: 25 passed, two opt-in native clipboard checks skipped. Quit casing/whitespace, retained failed submissions, disposal release and continued rejection of other slash commands are covered.
- Fresh-context adversarial review found no scoped issues after tracing routing, ownership, in-flight admission and shutdown. Source documentation, planning hygiene and diff whitespace validation passed.
- Validation used controlled clipboard/storage adapters and the real headless terminal input loop on Windows. Native clipboard behavior and other platforms were not revalidated. Quitting invokes no model, so no external model call was needed.
