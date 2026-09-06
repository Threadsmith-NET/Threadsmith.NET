# Plan 96 — Active-run steering and double-Escape cancellation

**Status:** Implementation complete.

**Delivery track:** Maintenance.

**Prerequisites:** Completed Plan 26 terminal ownership, completed Plan 80 active-turn continuation, completed Plan 88 corrective-loop substrate, and completed Plan 91 model-callable Explorer delegation.

## 1 Objective

Complete the deferred active-run interaction from Plan 91 without replacing PrettyPrompt, pinning a composer, using the alternate screen, or adding a second independent terminal reader. During an ordinary active conversation, `Enter` requests one idempotent steering pause and `Esc Esc` cancels the run.

## 2 Architectural Context

PrettyPrompt remains the full-text editor and native scrollback remains authoritative. The host owns run identity, cancellation, safe model/tool boundaries, delegation membership, steering order, sanitization, and model-context delivery. The TUI may observe keys only while PrettyPrompt is inactive and only through one public serialized `IConsole` owner.

## 3 Scope

- A serialized active-run input lease over PrettyPrompt's public `IConsole` abstraction.
- Standalone `Enter` steering requests with immediate acknowledgement and idempotent repetition.
- An 850 ms double-`Esc` cancellation chord.
- Safe-boundary pauses for ordinary model rounds, tool batches, every still-running Explorer child, and delegation join.
- The ordinary PrettyPrompt composer with a `steer >` label while the run is paused.
- Ordered parent/child delivery, visible-message archival, events, delegation accounting, tests, and operator documentation.

## 4 Non-Scope

- Mid-token provider interruption or suspension inside a tool invocation.
- A pinned footer/composer, alternate-screen UI, blind typing, or a second editor.
- Steering that expands tools, trust, paths, model, role, budget, approval, or mutation authority.
- General slash-command execution while paused; `/agents` inspection/cancellation is the supported active-run command surface.

## 5 Current State

Plan 91 shipped `delegate_agents` with zero steering counts because the TUI had no serialized input owner. `ConversationalShell` waited only for run, decision, and output-drain tasks, while `PrettyPromptConsoleSurface` exclusively read full composer input. Active cancellation used `Ctrl+C`.

## 6 Proposed Design

`BufferedPromptConsole` wraps PrettyPrompt's public `IConsole`. Its active-run lease polls keys only while PrettyPrompt is inactive. A standalone `Enter` is consumed as steering; two unmodified Escape keys within 850 ms request cancellation. Multi-key typed/pasted bursts and all other keys are buffered and replayed to the next PrettyPrompt read.

`RunSteeringCoordinator` registers each parent run and the exact children of the session-exclusive model-callable delegation. The first request creates one pause id. Repeated requests return that same id and publish no duplicate acknowledgement. A parent pauses after the current response/tool batch. A delegation pauses after every still-running child reaches its next pre-provider boundary or becomes terminal, and the joined tool result cannot return until the prompt is submitted or dismissed.

The `RunSteeringPauseRequested` event lets the TUI stop the current spinner, write `Steering request received; waiting for the current model/tool boundary.`, and resume the activity display. `RunSteeringPaused` is published only after the barrier is ready; rendering it ends transient activity and flushes all earlier output before `steer >` opens. No model or tool operation can start while that composer is visible.

## 7 Public Contracts

- `SteeringPauseId`
- `RequestRunSteeringPauseCommand` / `RunSteeringPauseRequestResult`
- `WaitForRunSteeringPauseCommand` / `RunSteeringPauseWaitResult`
- `SubmitRunSteeringCommand` / `RunSteeringSubmissionResult`
- `RunSteeringPauseRequested`, `RunSteeringPaused`, and `RunSteeringSubmitted`

## 8 Project/File Changes

- `Threadsmith.Core`: command/result ids, statuses, and event contracts.
- `Threadsmith.Execution`: run coordinator, parent/child safe boundaries, conversation archive/model messages, and joined steering accounting.
- `Threadsmith.Tui`: serialized PrettyPrompt input lease, active-run wait integration, acknowledgement, steering composer, and double-Escape routing.
- `Threadsmith.App`: one shared coordinator in the production composition root.
- Focused CoreRuntime and ParallelAgents tests plus user, operations, acceptance, manual, event-catalog, and TUI DOX updates.

## 9 Ordered Tasks

1. Add host command/event contracts and one run-scoped coordinator.
2. Integrate parent, child, and delegation-join safe boundaries.
3. Share the coordinator through production composition.
4. Wrap PrettyPrompt's public console and buffer non-hot-key input.
5. Add idempotent Enter acknowledgement, steering prompt, and double-Escape cancellation.
6. Add focused tests and synchronize implemented-behavior documentation.

## 10 Testing

- Repeated Enter returns one pause id, one acknowledgement, and one steering submission.
- The child barrier waits for paused or terminal children and reports delivered/undelivered counts.
- Empty input dismisses and releases a parent pause without adding context.
- Repeated buffered Enter produces one signal; double Escape produces cancellation; multiline bursts replay exactly.
- CoreRuntime, ParallelAgents, architecture/event serialization, and full solution build regressions pass.
- Manual verification follows MTP-254 and Scenario L.

## 11 Security/Permissions

Steering is sanitized user content at user authority. It cannot approve, mutate, widen child policy, add tools, change trust, or bypass normal model/tool/mutation boundaries. Events carry identities, sequence, and presence metadata; message bodies remain in the existing sanitized conversation archive rather than event payloads.

## 12 Observability

The TUI shows an active-run key hint, one immediate request acknowledgement, a distinct `steer >` prompt, and submitted/dismissed status. Durable events identify request, ready, and submission boundaries without storing steering text. `delegate_agents` reports submitted, delivered, and undelivered child-delivery counts.

## 13 Migration/Compatibility

No configuration or database migration is required. Existing headless callers continue to use cancellation normally and may use the new shared commands. Redirected/non-interactive input declines the active-run lease. `Ctrl+C` remains supported.

## 14 Acceptance Criteria

1. `Enter` during a run immediately acknowledges one pending request and repeated Enter has no downstream effect.
2. The steering composer appears only after the in-flight response/tool batch and all active delegation children reach safe boundaries.
3. The run is paused while `steer >` is displayed, so output cannot scroll the prompt away.
4. Submitted text reaches the parent and every eligible still-running child in sequence; completed children count as undelivered.
5. Empty/cancelled steering resumes without context; `/agents` retains inspection/cancellation semantics.
6. `Esc Esc` cooperatively cancels the active run and the ordinary composer returns after terminal rendering.
7. Ordinary typing and pasted multi-key bursts are replayed to PrettyPrompt without per-character editor replacement.

## 15 Risks

- Console implementations differ in how pasted keys are buffered. Multi-key batches are therefore preserved as composer input unless the complete batch consists only of Enter or Escape hot keys.
- Non-cooperative providers/tools still determine cancellation latency; the host never fabricates mid-operation suspension.
- A child may finish before steering submission; delivery accounting reports that honestly rather than reopening it.

## 16 Documentation

Update the user guide, keyboard reference, parallel-agent operations, event catalog, Scenario L, MTP-254, and the TUI ownership DOX. Keep completed Plan 91 and its recorded feasibility result unchanged.

## 17 Open Decisions

None for this maintenance item. A future permanently mounted composer would require a separate terminal architecture decision.
