# Conversation Context Operations

Threadsmith preserves bounded cross-turn continuity without replaying an unbounded transcript. The host archives only sanitized accepted user requests and final visible assistant responses. Hidden reasoning, provider payloads, raw tool output, and extension/provider types never enter durable conversation state.

## Modes

The compiled default is `ConversationAware`.

| Mode | Current turn | Recent complete turns | Structured/retrieved memory |
|---|---:|---:|---:|
| `ConversationAware` | Yes | Bounded by age, count, and tokens | Yes |
| `GovernedMemoryOnly` | Yes | No | Yes |
| `Stateless` | Yes | No | No |

Changing mode affects the next request and does not delete archive, memory, provenance, or summaries.

Interactive commands:

```text
/context mode
/context mode conversation-aware
/context mode governed-memory
/context mode stateless
/context inspect
/context compact
```

Headless integrations use the same host-owned contracts:

- `SetConversationContextModeCommand`
- `GetConversationStateCommand`
- `GetContextInspectionCommand`
- `RequestConversationCompactionCommand`

`HeadlessShell.WriteContextInspectionAsync` emits the shared inspection projection as stable JSON.

## Inspection

`/context inspect` reports:

- effective mode and configuration/session source;
- current archived message identity;
- summary version and compacted-through sequence;
- included and omitted recent messages;
- included, retrieved, stale, superseded, and invalid memory;
- source message, run, and evidence identifiers;
- deterministic retrieval score and rationale;
- category token accounting and exact pressure reductions;
- context-window pressure and the next compaction recommendation.

Inspection contains metadata, bounded sanitized memory content only in the assembled prompt, and no secret/provider/tool payloads.

## Compaction and failure behavior

Compaction runs only at a host-owned turn boundary. One operation per session may run at a time. The host bounds source messages, input tokens, candidate item count, item length, retries, and provider calls. Candidates remain untrusted until schema, sanitization, authority, provenance, evidence revision, supersession, and cycle validation pass.

The compacted-through sequence never advances beyond the exact retained source range. Cancellation, malformed output, unsupported provenance, provider failure, oversize oldest input, or persistence failure leaves the prior snapshot active. Automatic compaction failure is logged without failing an otherwise successful conversation.

Compaction never deletes archived messages. Full bodies have an independent retention age; ordered metadata, hashes, structured memory, and provenance remain restorable.

## Configuration

Configure repository overrides under `context:conversation` in `.threadsmith/config.*`. The complete schema is in `.threadsmith/config.example`.

Compiled defaults:

| Setting | Default |
|---|---:|
| `mode` | `ConversationAware` |
| `recentTurnTokens` | 8,000 |
| `summaryTokens` | 4,000 |
| `retrievedMemoryTokens` | 4,000 |
| `recentTurnCount` | 12 |
| `recentTurnMaximumAge` | 7 days |
| `compactionPressurePercent` | 75 |
| `maximumRetrievedItems` | 24 |
| `artifactThresholdCharacters` | 16,384 |
| `compaction.archivedTokenThreshold` | 8,000 |
| `compaction.archivedMessageThreshold` | 12 |
| `compaction.maximumActiveMemoryItems` | 128 |
| `compaction.maximumCandidateItems` | 64 |
| `compaction.maximumItemCharacters` | 2,000 |
| `compaction.maximumSourceMessages` | 100 |
| `compaction.maximumProviderRetries` | 1 |
| `compaction.maximumProviderCalls` | 2 |
| `compaction.maximumInputTokens` | 16,000 |

Invalid enum values, non-positive budgets, pressure outside 1–100%, or retries exceeding the provider-call cap fail before model invocation.

## Retention and restoration

Persistence migration 2 owns archive, mode, memory, provenance-edge, and summary tables. Large sanitized bodies use the content-addressed artifact store and are hash-verified during restoration. Unknown future schemas and missing/corrupt artifacts produce bounded warnings rather than fabricated content.

`RetentionOptions.ConversationMessageBodyAge` defaults to 30 days independently of session-event retention. `RetainConversationBodies` can preserve full bodies. Structured memory and provenance remain cold durable state when bodies expire.

## Verification

Automated acceptance coverage is in `Threadsmith.ConversationContext.Tests`, including named Scenario I coverage for promotion, compaction, repository invalidation, all three modes, restart restoration, deterministic retrieval, prompt-injection escaping, pressure, cancellation/failure fallback, and TUI/headless command parity. Maintained terminal checks are in `docs/implementation-plans/manual-test-plan.md` §12.
