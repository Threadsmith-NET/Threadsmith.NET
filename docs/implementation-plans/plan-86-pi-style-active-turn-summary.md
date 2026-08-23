# Implementation Plan 86: Pi-Style Active-Turn Summary

**Status:** Complete — Pi-style active-turn summary compaction, tests, and documentation are in place

**Delivery track:** Maintenance — replace the ineffective Plan 80 fact-list summary
**Strategy source:** Shared Context §A.2; Pi coding-agent compaction behavior reviewed from installed version 0.84.2
**Prerequisite plans:** Plan 80

## 1. Objective

Replace the current fact-list/FIFO behavior with the simple approach used by Pi:

1. keep recent tool calls and results unchanged;
2. send the previous summary and newly old material to the summary model;
3. ask the model for one updated summary;
4. replace the previous summary and newly summarized material with that updated summary;
5. repeat the same process the next time compaction is needed.

The goal is to reduce prompt size without making the model rediscover the files, findings, decisions, and next steps it was already using.

## 2. Architectural Context

Pi's default compaction is simple and works well:

- it keeps a recent portion of the conversation unchanged;
- it gives the previous summary and newly removed messages to the model;
- the model writes an updated structured summary;
- it carries forward a list of files read and changed;
- the full session history remains available outside the shortened model prompt.

Threadsmith already has the difficult surrounding mechanics: deciding when to compact, selecting complete old tool groups, keeping recent groups unchanged, using a separate summary profile, measuring before/after size, and continuing the same turn.

The current summary step is the problem. The model can select only new fact IDs. The host then appends those facts and removes the oldest previous facts. In the latest run, each new summary kept 28 new facts and only four older facts. That reduced token counts but caused repeated file reads.

This plan changes the summary step. It does not rebuild the rest of Plan 80.

## 3. Scope

- Keep the current pressure trigger and recent-raw retention behavior.
- Keep selecting only complete, previously delivered tool-call/result groups.
- Send the complete previous summary to the summary model.
- Send the newly selected old tool groups in readable chronological form.
- Include the current task, important user constraints, and a cumulative list of files read or changed.
- Ask for one updated Markdown summary.
- Use a short fixed summary format based on Pi's handoff format.
- Replace the previous summary and selected old groups with the returned summary.
- Keep newer raw tool groups unchanged.
- Use the existing summary profile, request counting, activity display, and before/after size checks.
- Keep the original tool history in the existing run records.
- Implement functionality first, then stop for user review.
- Write or update tests only after the user signs off on the behavior.
- Write or update product documentation only after the user signs off on the behavior.

## 4. Non-Scope

- No fact scoring system.
- No FIFO fact queue.
- No selectable fact IDs.
- No categories or source objects in the model response.
- No database-backed working-set manager.
- No second summary model or multi-step summary debate.
- No additional configuration options beyond trusted summary budget and model-output percentage.
- No changes to tool execution, approvals, mutation behavior, or the current user request.
- No acceptance-scenario or manual-test rewrite unless observable user/operator workflows change.

## 5. Current State

Plan 80 currently creates a bounded list of exact facts. The summary model selects facts only from the newest material. Previous facts are carried automatically, then the oldest previous facts are removed until the list fits.

This is predictable, but it does not preserve the model's working state. The latest real run compacted successfully three times and saved substantial prompt space, but every exact repeated tool call occurred after a compaction. The feature therefore does not meet its practical goal.

The existing trigger, complete-group selection, profile routing, request accounting, failure handling, TUI activity, and history replacement can be reused.

## 6. Proposed Design

### 6.1 Material sent to the summary model

Build one summary request containing, in this order:

1. a short instruction describing the requested summary format;
2. the current task and user constraints;
3. the complete previous summary, when one exists;
4. the newly selected old tool calls and results, in their original order;
5. the cumulative files-read and files-changed lists.

The previous summary is required input, not optional filler. If the candidate request cannot fit, summarize fewer old groups. Never compact a group that was not sent to the summary model.

Do not send the recent raw groups being retained; the main model will still receive those unchanged.

### 6.2 Summary format

Ask the model for plain Markdown with these sections:

- Goal
- User constraints
- Progress
- Important findings and decisions
- Current working set
- Next steps
- Files read
- Files changed

The prompt should say:

- update the previous summary rather than merely summarizing the newest messages;
- keep older details that are still needed;
- remove details that are no longer useful;
- preserve exact file paths, symbol names, commands, errors, and unresolved questions when they matter;
- do not include commentary about the act of summarizing.

The response is summary text, not JSON and not a list of fact IDs.

### 6.3 Applying the summary

Accept a response only when it:

- completed normally;
- is not empty;
- fits the configured total summary budget;
- respects the request-specific model-output ceiling;
- reduces the rebuilt main request.

On success:

1. remove the previous summary and the old groups that were sent for summarization;
2. insert the returned summary as one assistant-history message;
3. leave all newer raw groups unchanged;
4. continue the same model turn.

On the next compaction, send that returned summary in full along with the next set of old groups. There is no separate merge or FIFO pruning step.

### 6.4 Files read and changed

Follow Pi's useful working-set behavior:

- collect file paths from completed file tool calls;
- keep separate deduplicated lists for files read and files changed;
- carry the lists across repeated summaries;
- include them in the summary request and final summary;
- count them inside the existing summary-size limit.

Do not add a more elaborate relevance system initially. The model can decide which file details belong in the main summary, while the short file lists make the working set visible.

### 6.5 Failure behavior

If the summary request fails, is cancelled, returns nothing, is cut off, is too large, or does not reduce the rebuilt request, keep the existing conversation unchanged.

Do not add a correction conversation or multiple summary passes initially. A later normal compaction attempt may try again using the existing delay/backoff behavior.

## 7. Public Contracts

Prefer small changes to the current internal Plan 80 types. The summary request needs only:

- current task text;
- previous summary text, if any;
- newly selected complete groups;
- files read;
- files changed;
- maximum input and output sizes.

The result needs only:

- summary text;
- observed token usage.

Keep existing before/after metrics, profile identity, summary version, covered group range, and completion events where they still apply. Do not add a new public framework for this change.

## 8. Project/File Changes

Before functionality signoff, expected source changes are limited to:

- `src/Threadsmith.Context/ActiveTurnCompaction.cs` — replace fact-ID candidate generation and FIFO merging with previous-summary-plus-new-material summarization;
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs` — only if the simpler summary result requires a small integration change;
- existing model request types only where needed to carry summary text.

Do not change tests or product documentation during the functionality trial.

After signoff, the likely test and documentation files are listed in Sections 10 and 16.

## 9. Ordered Tasks

### Functionality trial

1. Get user approval of this plan and the proposed summary sections.
2. Record the latest real-run baseline: three compactions, token savings, 22 exact repeated tool calls, and 20 repeated reads.
3. Replace the fact-ID response with plain summary text.
4. Always send the full previous summary plus newly selected old groups.
5. Remove FIFO fact merging.
6. Add the Pi-style files-read/files-changed carry-forward lists.
7. Build only the affected projects needed to start Threadsmith.
8. Run one real-provider long-turn trial.
9. Report the actual summaries' sizes, compaction savings, repeated reads, and whether the model kept its working state.
10. Stop and wait for explicit user signoff.

### After functionality signoff

11. Write/update the focused tests listed in Section 10.
12. Run the focused suites and fix confirmed defects.
13. Update only the documentation listed in Section 16.
14. Run the broader build/test/format checks.
15. Update this plan's status only after the signed-off behavior and deferred verification pass.

Do not commit the functionality trial as complete before the deferred tests and documentation are finished.

## 10. Testing

After signoff, add or update tests for these specific cases.

### Summary input

1. **First compaction has no previous summary** — the request contains the task, selected old groups, and file lists.
2. **Later compaction includes the full previous summary** — no prior summary section or item is silently omitted by the input builder.
3. **Later compaction includes new groups after the previous summary** — the model receives both old summarized state and new work in the right order.
4. **Only material actually sent can be compacted** — if the summary-model input limit is reached, the selected group prefix shrinks.
5. **Current and recent raw messages are excluded from summary input** — they remain in the main request unchanged.
6. **File lists carry forward** — paths remain deduplicated across repeated compactions and reads/changes remain separate.

### Summary replacement

7. **First returned summary replaces the selected old prefix.**
8. **Second returned summary replaces both the first summary and the next selected prefix.**
9. **The model can keep an important older detail** — a scripted second summary that repeats an older detail retains it.
10. **The model can drop an obsolete older detail** — a scripted second summary that omits it removes it.
11. **No FIFO merge remains** — the result is exactly the returned updated summary, not prior items plus new items.
12. **Recent raw tool groups remain byte-for-byte unchanged.**
13. **Tool calls and matching results are never split or reordered.**
14. **A newly completed group is shown to the main model at least once before it can be summarized.**

### Bounds and failures

15. **Empty or whitespace summary leaves the conversation unchanged.**
16. **Cut-off summary leaves the conversation unchanged.**
17. **Oversized summary leaves the conversation unchanged.**
18. **Provider failure leaves the conversation unchanged.**
19. **Cancellation leaves the conversation unchanged.**
20. **Zero or negative token savings leaves the conversation unchanged.**
21. **A rebuilt request above the pressure target still activates when it reduces the request.**
22. **A successful summary increments the history-rewrite generation once.**
23. **A failed summary does not increment the generation.**

### Provider and accounting

24. **Configured summary profile is used.**
25. **Main-profile fallback still works when no summary profile is configured.**
26. **Summary requests contain no tool definitions and cannot start tool calls.**
27. **Usage, call count, and elapsed time are recorded for the actual summary request.**
28. **Before/after token counts and displayed savings match the rebuilt request.**

### End-to-end continuity

29. **Three scripted compactions carry a working file/symbol/task across all three summaries.**
30. **A long tool turn continues after each summary and reaches a final answer.**
31. **The current user request and system messages remain unchanged through repeated compaction.**
32. **The real-provider comparison shows at least 50% fewer exact post-compaction rereads than the recorded baseline, with equal or better final-answer usefulness.**

The first 31 cases should be automated after signoff. Case 32 is a real-provider acceptance run and should be documented only after the behavior is approved.

## 11. Security/Permissions

Use the same cleaned tool results already sent to the main model. Do not add secret values or raw provider payloads to the summary request.

The summary is assistant history, not a replacement for system messages or the current user request. Tool availability, approvals, and file access continue to work exactly as they do now.

## 12. Observability

Keep the existing compacting activity and completion message. Continue recording:

- summary profile;
- before and after token counts;
- tokens and percentage saved;
- duration;
- completed, failed, cancelled, or zero-savings outcome;
- summary version and covered group range for context inspection.

Do not put summary text or tool-result text in normal logs or the TUI completion message. The existing explicit raw model log remains available for user-requested diagnosis.

## 13. Migration/Compatibility

No stored-data migration is expected. Active-turn summary state is rebuilt during the current process and the original run/tool records remain available.

The ordinary model request still receives one assistant summary followed by recent raw tool groups. Existing model providers should not need provider-specific changes.

The existing fact-ID implementation remains available in Git history and in rollback checkpoint `93437cd` while this replacement is evaluated.

## 14. Acceptance Criteria

- The first compaction sends new old material and receives one useful summary.
- Every later compaction sends the complete previous summary plus newly old material and receives one updated summary.
- The returned updated summary, not FIFO host merging, decides what prior information remains.
- Recent raw tool calls/results stay unchanged.
- Only complete groups actually shown to the summary model are removed from the main request.
- Files read and changed remain visible across repeated summaries.
- The same active turn continues after compaction.
- Failed or unhelpful compaction leaves the current conversation unchanged.
- Three real-provider compactions preserve the current task, working files/symbols, important findings, and next steps.
- The real-provider comparison produces at least 50% fewer exact post-compaction rereads than the recorded baseline without making the final answer worse.
- Functionality signoff precedes the deferred tests and product documentation.
- The listed tests, required documentation, affected builds, formatting, and repository checks pass.

## 15. Risks

- **The model writes a poor summary:** keep recent raw groups, use a clear fixed prompt, and evaluate one real run before writing tests or docs.
- **The summary forgets an important detail:** the next prompt includes the full previous summary and asks the model to keep what still matters; the real-run reread comparison is the deciding check.
- **The summary limit is too small:** default to the approved 16,384-token total budget with an 80% model-output partition; change only with trusted configuration evidence.
- **The files list becomes too large:** count it inside the existing summary limit and add a simple bound only if the trial requires one.
- **Old tests describe the fact-ID behavior:** do not rewrite them during the trial; replace them only after the user approves the new behavior.
- **Scope expands again:** do not add scoring, ranking, retrieval, correction loops, or new settings during the first implementation.

## 16. Documentation

After signoff, update only what the approved behavior requires:

- `docs/operations/conversation-context.md` — describe previous-summary-plus-new-material behavior and recent raw retention;
- `docs/user-guide.md` — describe the short user-visible behavior and failure outcome;
- `docs/implementation-plans/README.md` — add the required navigation row for this maintenance plan;
- Plan 80 only if a short factual supersession note is needed;
- applicable `AGENTS.md` files only if their durable guidance is no longer correct;
- acceptance scenarios and manual test plan only after the real behavior is approved and a stable user check exists.

Do not rewrite unrelated architecture or historical planning documents.

## 17. Open Decisions

Resolve these at the functionality-signoff checkpoint, not before implementation:

- Whether the proposed Markdown sections need a small wording change after seeing the first real summary.
- Whether the 16,384-token default total summary budget and 80% model-output partition remain sufficient across more real runs.
- Whether the Pi-style files-read/files-changed lists need a simple maximum.
- Whether the 50% reread-reduction target should be raised after the first successful comparison.
