# Resumed conversation output

**Status:** Complete
**Delivery track:** Maintenance
**Prerequisites:** Existing durable session lifecycle, sanitized conversation archive, frontend-neutral interaction, and retained TUIKit output.

## 1 Objective

On successful interactive resume, replace all retained output with saved user messages and assistant replies and follow the latest exchange.

## 2 Architectural Context

Retain the existing session transition authority, command dispatcher, conversation archive reader, semantic presentation batches, model answer collector, and transcript projection. No historical execution events are published.

## 3 Scope

Direct, picker, and current-session resume; chronological visible history; complete output replacement; safe Markdown/source rendering; bounded recent reads; unavailable-body placeholders and omission warnings.

## 4 Non-Scope

New/clone output behavior, durable archive retention, model context selection, historical tool activity, reasoning, and original terminal scrollback.

## 5 Current State

Resume restores engine state but previously printed only a confirmation. Ctrl+L hides output while retaining earlier content, so it cannot implement replacement.

## 6 Proposed Design

After a successful transition, query a recent archive window through `GetConversationStateCommand`. Reuse the archive reader and artifact hash checks, reading newest messages first up to a SQL row limit and cumulative character budget, then returning chronological order. Present user messages through the live input echo item and assistant replies through `ModelAnswerCollector`. A replacement batch discards retained source, layout, links, selection, and scroll state and follows incoming output.

## 7 Public Contracts

`ConversationHistoryWindow` provides optional message and body-character limits for archive queries. Null keeps existing complete snapshot semantics. `PresentationUserInputItem` shares live and restored input rendering. `PresentationBatch.ReplaceOutput` replaces retained output at its destination.

## 8 Project/File Changes

Core conversation contracts; execution query forwarding; persistence archive reader; interaction shell/coordinator and presentation contracts; TUIKit transcript; existing archive stubs; focused command and archive regressions.

## 9 Ordered Tasks

1. Extend the established archive query with a bounded presentation window.
2. Share committed user-message presentation and add complete transcript replacement.
3. Wire successful resume and preserve existing output on rejected transitions.
4. Verify command, archive, rendering, architecture, and documentation contracts.

## 10 Testing

Cover direct/picker/current resume, chronological order, tail positioning, Home/resize, selection removal, empty archives, unavailable bodies, transition rejection, artifact restoration, character limits, and surrogate boundaries. Run the solution build and affected test projects.

Verification: solution build passes with zero warnings/errors; all seven focused resume cases, 661 core-runtime tests, 199 conversation-context tests, 97 persistence tests, and 326 architecture tests pass (three tests skipped). An earlier broad interaction run encountered an unrelated theme-settings file-move access error; that test passed in isolation and the complete interaction rerun passed. Documentation packaging validation and `git diff --check` pass. Evidence is under ignored `artifacts/session-resume-*.log`. Source review traced direct/picker/current resume, the query handler and shared archive reader, MAIN routing, complete source/layout/selection reset, and resize reprojection. No new execution path, model request, event replay, or durable transcript store was introduced. Real-terminal interaction was not exercised.

## 11 Security/Permissions

Keep repository-scoped resume authorization and sanitization unchanged. Render only visible archive roles; do not replay tool operations, provider state, or reasoning. Propagate cancellation.

## 12 Observability

Retain the resume confirmation and transition warnings. Disclose unavailable bodies, omitted earlier text, and omitted older messages.

## 13 Migration/Compatibility

No database migration. Existing complete archive/context reads retain their behavior. The view remains bounded and independent of durable history.

## 14 Acceptance Criteria

Scenario V and MTP-247 own the observable behavior and executable checks. Successful resume removes prior output permanently from the view, shows available saved messages, and follows the latest exchange. Failed transitions preserve output.

## 15 Risks

Oversized messages can begin with a partial body in the bounded view; omissions are explicit. External artifact bodies use the existing full read/hash validation before trimming, so one artifact can transiently exceed the returned window budget. Real-terminal interaction remains a manual check.

## 16 Documentation

Update the user guide, session lifecycle operations, Scenario V, MTP-247, and one navigation row here. Completed milestone contracts remain frozen.

## 17 Open Decisions

None.
