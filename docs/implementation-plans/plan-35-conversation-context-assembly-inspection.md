# Plan 35 — Conversation Context Assembly, Modes, and Inspection

**Milestone:** 7.4 (Cross-Turn Conversation Context and Compaction)
**Prerequisites:** plan-34; plan-26 for context/usage status projections
**Depends on by:** future conversation search/export and controlled subagent memory plans
**Status:** Complete.

**Implementation result:** Validated layered policy, three context modes, bounded complete-turn/summary/retrieval assembly, deterministic pressure reduction, expanded inspection, shared TUI/headless host commands, safe-boundary scheduling, and focused Plan 35 tests are implemented.

## 1 Objective

Integrate bounded recent turns, structured session memory, retrieval, compaction pressure, and the three conversation modes into request assembly; expose configuration, session controls, and exact inspection of included, compacted, retrieved, invalidated, and omitted history across interactive and headless operation.

## 2 Architectural Context

Plans 33–34 establish archive, memory, compaction, retrieval, and invalidation. This plan changes actual model requests. The current `ContextAssembler` explicitly reports `ConversationHistoryIncluded = false`, scopes most evidence to the current run, and budgets project prompt, phase instructions, task, evidence, tools, and schema. Conversation-aware assembly must preserve those governed categories while giving the current user turn and conversational continuity explicit budgets and reduction priority.

The host remains authoritative. Mode selection cannot weaken sanitization, provider sensitivity policy, hard context-window limits, repository invalidation, or provenance.

## 3 Scope

- Make `ConversationAware` the compiled default.
- Add normal layered configuration for mode and bounded conversation/summary/retrieval budgets.
- Add session override via extensible slash/headless commands.
- Include the current user turn verbatim in every mode.
- In Conversation-aware mode, include a bounded recent-turn window, active structured summary, and relevant retrieved older memory.
- In Governed-memory-only mode, exclude raw prior turns and include only active summary/retrieved governed memory.
- In Stateless mode, include only current-run state and current user turn; archive may continue for audit but is not selected.
- Apply deterministic pressure reduction while preserving explicit user decisions/corrections.
- Expand `ContextInspectionProjection` and `/context` output with conversation categories and provenance.
- Keep provider capability/context-window negotiation and usage/footer projections accurate.
- Add restoration, mode-switch, TUI/headless parity, and E2E acceptance coverage.

## 4 Non-Scope

- Deleting or exporting conversation archives.
- Cross-session/user-global memory.
- Team-shared memory.
- Embeddings/vector databases.
- Feeding hidden reasoning into later requests.
- Provider-managed server-side conversation IDs as authoritative state.
- Allowing a mode to bypass sensitive-data provider policy.

## 5 Current State

`ContextAssembler` builds stable policy, project prompt, phase instructions, current task, current-run evidence, tools, and output schema. `ContextInspectionProjection` reports category tokens and omissions but hard-codes conversation history as absent. `/context` exposes current effective usage. Session model preferences and usage restore independently of conversation state.

## 6 Proposed Design

### 6.1 Configuration

Normal layered keys, with hard validated bounds, include illustrative names:

```json
{
  "context": {
    "conversation": {
      "mode": "conversationAware",
      "recentTurnTokens": 8000,
      "summaryTokens": 4000,
      "retrievedMemoryTokens": 4000,
      "recentTurnCount": 12,
      "compactionPressurePercent": 75,
      "maximumRetrievedItems": 24
    }
  }
}
```

Implementation must document exact defaults/ranges in `.threadsmith/config.example`. Layer precedence is normal. Session mode override is durable session state but does not rewrite repository/user configuration unless the existing configuration mutation workflow explicitly does so.

### 6.2 Mode behavior

#### Conversation-aware (default)

Assembly order:

1. Stable host policy.
2. Project prompt append content.
3. Phase instructions.
4. Current user turn verbatim.
5. Explicit active user requirements/decisions/corrections and constraints.
6. Bounded recent raw user/assistant turns, newest complete turns first but rendered chronologically.
7. Active structured summary by category.
8. Relevant retrieved older memory with provenance.
9. Current governed task/evidence/tools/output schema.

#### Governed memory only

Exclude all raw prior messages. Include current turn, active structured summary items, retrieved valid memory, and current-run governed state. Inspection must state that raw recent turns were excluded by mode, not by token pressure.

#### Stateless

Include current turn and current-run governed state only. Exclude prior raw turns, summary, and retrieved memory. Archiving remains enabled for audit/restoration unless retention policy disables bodies; mode switching later may reuse previously governed memory.

### 6.3 Token pressure and reduction

Reserve framing, current-turn, tool-schema, and output-schema budgets before optional history. When pressure rises:

1. Keep current user turn verbatim or fail before model invocation if it cannot fit safely.
2. Keep explicit active user decisions/corrections verbatim within hard item bounds.
3. Reduce duplicate/redundant current-run evidence.
4. Reduce oldest raw recent turns as whole turn pairs; never slice into misleading fragments.
5. Replace eligible older raw turns with the active structured summary.
6. Reduce lower-ranked retrieved memory.
7. Reduce repository findings/completed-work items before active requirements/constraints/decisions.
8. Omit optional prompt/evidence according to existing phase policy.
9. Fail with an inspectable bounded-context error rather than silently dropping required policy/current task/schema.

Compaction is scheduled for the next safe turn boundary when pressure crosses policy; request assembly never waits indefinitely for compaction.

### 6.4 Rendering and prompt safety

Conversation messages and memory render in explicit host-delimited sections with message/memory IDs, role/category, and sanitized content. Untrusted historical content cannot claim system/developer priority or override current host policy. Provider-specific message DTOs are created only inside adapters after host assembly.

### 6.5 Inspection

Extend context inspection with:

- effective mode and source layer/session override;
- current message ID/run ID;
- recent turns included/omitted and token totals;
- active summary snapshot/version and category counts;
- retrieved memory IDs, categories, scores/rationale, and source provenance;
- compacted source ranges;
- stale/superseded/excluded-by-mode items;
- pressure reductions in exact order;
- next compaction trigger/status;
- total effective context versus selected model window.

Content previews remain sanitized and bounded. Full archived bodies require existing artifact/session access policy.

### 6.6 User controls

Extend `/context` with keyboard-friendly commands such as:

- `/context mode` — show effective mode and source;
- `/context mode conversation-aware`;
- `/context mode governed-memory`;
- `/context mode stateless`;
- `/context inspect` — show last request composition and provenance;
- `/context compact` — request safe turn-boundary compaction, subject to budget/policy.

Headless equivalents use host commands/options and produce stable structured output. Exact names may follow existing slash-command conventions but all three modes and inspection must be available without editing JSON.

## 7 Public Contracts

Add or extend host-owned contracts for:

- session mode query/change;
- conversation context policy snapshot;
- context categories for current turn, recent turns, summary, and retrieved memory;
- inclusion/omission/reduction rationale;
- compaction status/request;
- conversation-aware context usage projection.

Do not expose raw provider messages, summary-provider output, terminal segments, persistence rows, or extension types.

## 8 Project/File Changes

- `Threadsmith.Context` — conversation-aware assembly categories, budgeting, rendering, inspection.
- `Threadsmith.Execution` — pass authoritative current message/run and session mode; schedule compaction.
- `Threadsmith.Core` — commands/events/projection extensions.
- `Threadsmith.Tui` — `/context` mode/inspect/compact and selector/status behavior.
- `Threadsmith.Cli` — headless mode/inspection commands and stable output.
- `Threadsmith.Models` — context-window negotiation inputs remain provider-neutral.
- `Threadsmith.Persistence` — restore session override and last inspection/compaction status as required.
- `.threadsmith/config.example`, user guide, operations docs, manual test plan, acceptance scenarios, DOX.

## 9 Ordered Tasks

1. Add bounded conversation context configuration with compiled defaults and validation.
2. Extend context categories, inspection DTOs, usage projection, events, and serialization.
3. Modify request assembly to accept authoritative current message, mode, archive window, summary, and retrieval result.
4. Implement Conversation-aware ordering and recent complete-turn selection.
5. Implement Governed-memory-only and Stateless exclusion policies.
6. Implement deterministic token-pressure reduction and safe failure when required framing/current input cannot fit.
7. Schedule compaction at safe turn boundaries without blocking/corrupting the active request.
8. Update model context-window negotiation and session/footer context usage.
9. Add mode/query/inspect/compact host commands and TUI/headless surfaces.
10. Restore session mode and memory/inspection state across restart.
11. Add fake-model request-capture, mode, pressure, provenance, invalidation, security, restoration, TUI, CLI, and E2E tests.
12. Update examples, user/operations docs, maintained manual tests, acceptance Scenario I, milestone status, and DOX.

## 10 Testing

Automated tests must verify:

- Conversation-aware is the default with no configuration.
- Current user turn is byte-for-byte preserved after sanitization in every mode.
- Recent complete turns are bounded, selected newest-first, and rendered chronologically.
- Explicit user decisions/corrections survive pressure before ordinary history/findings.
- Governed-memory-only includes no raw prior messages.
- Stateless includes no prior turns, summary, or retrieved memory.
- Mode changes affect the next turn and restore across restart.
- Cross-turn questions retrieve relevant decisions/constraints/findings/unresolved questions with provenance.
- Superseded and invalidated memory is excluded and inspection explains why.
- Pressure reduction follows documented order and never drops host policy/current task/required schema silently.
- Compaction failure does not block ordinary conversation using the prior snapshot.
- TUI/headless assemble identical model requests for equivalent commands.
- Context usage and model-window projection match the assembled request.
- Prompt-injection text in archived messages cannot escape history delimiters or override policy.
- Secret canaries and hidden reasoning never appear in assembled history, inspection, logs, snapshots, or diagnostics.

## 11 Security/Permissions

All historical content is untrusted input. Sanitize and delimit it; current host policy always precedes and outranks it. Mode and budgets cannot disable sensitive-data provider restrictions, hard token/byte/item limits, invalidation, provenance, secret redaction, or hidden-reasoning exclusion. `/context compact` does not authorize repository/tool mutation.

## 12 Observability

Emit mode, source, category token totals, selected/omitted counts, rationale codes, pressure percentage, reduction actions, compaction scheduling/outcome, summary version, retrieval counts, and model-window utilization. Never emit message/memory bodies, raw prompts, hidden reasoning, secrets, provider wire data, or full artifact content.

## 13 Migration/Compatibility

Existing configuration receives `ConversationAware` by compiled default. Existing sessions without archive use empty history until new turns are captured. Existing `/context` behavior remains available and gains subcommands/fields. Headless output schema changes require a new schema version while retaining tolerant readers.

Users requiring previous deterministic behavior can select `Stateless`. Sensitive environments can select `GovernedMemoryOnly`. Switching modes never deletes data; retention remains separate.

## 14 Acceptance Criteria

- All three modes are configurable through normal layering and session controls.
- Conversation-aware is default and includes bounded recent turns, typed summary, and relevant older memory.
- Governed-memory-only includes no raw prior messages.
- Stateless reproduces current-run-only behavior.
- Current user input and explicit decisions/corrections receive the documented preservation priority.
- Compaction/retrieval/invalidation provenance is visible in context inspection.
- Full archived messages remain durable according to retention and are never deleted by compaction.
- Interactive/headless parity, restoration, context-window accounting, cancellation, and security tests pass.

## 15 Risks

- Recent raw history can carry prompt injection. Mitigate with explicit untrusted delimiters, policy precedence, sanitization, and bounded windows.
- Default conversation awareness changes token/cost use. Expose usage and bounded defaults; retain two stricter modes.
- Overly aggressive pressure reduction harms continuity. Preserve typed priorities and make omissions inspectable.
- Mode semantics could diverge between TUI and headless. Use one host command/context assembly path.
- Summary and retrieval may duplicate recent turns. Deduplicate by source/message/content identity before budgeting.

## 16 Documentation

Update `.threadsmith/config.example`, `docs/user-guide.md`, context operations guidance, `/context` reference, manual test plan, acceptance Scenario I, ADR-31 implementation status, milestone/README status, and all affected DOX indexes. Clearly distinguish archive, governed memory, recent raw turns, compaction, retrieval, and deletion/retention.

## 17 Decisions

- Compiled defaults are fixed and validated: 8,000 recent-turn tokens, 4,000 summary tokens, 4,000 retrieved-memory tokens, 12 complete turns, a seven-day hot raw-turn window, 24 retrieved items, and 75% pressure. Compaction defaults are 8,000 archived tokens or 12 messages, 128 active items, 64 candidates, 2,000 characters per item, 100 source messages, one retry/two calls, and 16,000 input tokens. Context inspection exposes actual pressure so later tuning remains evidence-driven.
- Cross-session/global user memory remains explicitly out of scope and is deferred to a separate privacy/policy milestone; Milestone 7.4 stores session-owned state only.
