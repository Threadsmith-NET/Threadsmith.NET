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

## Repository-scoped memory

Repository memory is local, repository-scoped, and stored in the ignored repository database at `.threadsmith/threadsmith.db`. It is separate from session conversation memory: `/new`, `/resume`, process restart, and independent sessions opened against the same repository identity can reuse it, but it is not shared/team memory and is not tracked by Git.

Interactive commands:

```text
/memory remember repo <text>
/memory list repo [active|stale|superseded|forgotten|rejected|all]
/memory inspect <memory-id>
/memory supersede <memory-id> <replacement-text>
/memory forget <memory-id>
/memory validate repo
```

Headless integrations use the same host-owned contracts:

- `RememberRepositoryMemoryCommand`
- `ListRepositoryMemoryCommand`
- `InspectRepositoryMemoryCommand`
- `SupersedeRepositoryMemoryCommand`
- `ForgetRepositoryMemoryCommand`
- `ValidateRepositoryMemoryCommand`

Explicit commands sanitize and bound text, attach user-command provenance, preserve inactive audit rows, and publish metadata-only events. Context assembly retrieves only active items for the current repository identity, applies authority/relevance ranking plus item/token budgets, treats content as untrusted prompt data, propagates sensitivity, and reports every included and omitted repository-memory item in inspection. Repository-dependent items with path, symbol, project, or revision support become stale after matching host-observed repository mutations and remain omitted until corrected or explicitly validated.

Model-proposed repository-memory candidates are disabled in this implementation. Assistant prose, repository files, prompt appends, skills, hooks, and repository configuration cannot create or authorize durable repository memory.

## Inspection

`/context inspect` reports:

- effective mode and configuration/session source;
- current archived message identity;
- summary version and compacted-through sequence;
- included and omitted recent messages;
- included, retrieved, stale, superseded, and invalid conversation memory;
- included, omitted, stale, superseded, forgotten, and budget-excluded repository memory;
- source message, run, and evidence identifiers;
- deterministic retrieval score and rationale;
- category token accounting and exact pressure reductions;
- context-window pressure and the next completed-turn compaction recommendation;
- the latest active-turn pre-sampling estimate, pressure target, main-profile output reserve, configured/effective retention, candidate profile identity, eligible/compacted/retained group counts and tokens, summary/pruned/history generation, cut range, backoff, and classified outcome.

Inspection contains metadata, bounded sanitized memory content only in the assembled prompt, and no secret/provider/tool payloads.

## Active-turn tool continuation compaction

Every ordinary multi-round evidence/planning request is estimated with the canonical provider-wire estimator before sampling. Tool calls and matching results are retained as complete chronological groups. A newly completed group must reach the model exactly in a later completed request before it can enter an eligible oldest prefix. Current user input, host/repository instructions, output contracts, tool definitions, and the initially assembled prefix remain unchanged.

At 75% of the active main profile's effective input budget, after honoring that profile's effective request output reserve, the host may replace an older delivered prefix with one validated cumulative summary. The configured 12,000-token newest-raw target scales down when the main profile, frozen request cost, summary reserve, or minimum-savings gate leaves less capacity; at least the newest complete group remains exact. Activation still requires at least 4,096 estimated tokens of savings and bounds summaries to 4,096 estimated tokens. Candidate input is bounded by both the 32,000-token host ceiling and the candidate profile's context window minus its effective output reserve, with one global projection budget across at most 48 source groups, 512 call/result messages, 32 items, 2,000 characters per item, one retry, and two provider calls. When no separate candidate profile is configured, the active main profile remains the fallback. Repository content cannot disable, delay, or lower the pressure boundary.

An optional repository-excluding trusted setting may route candidate calls to a different configured model profile or provider. Resolution and dispatch use a repository-excluding user/machine/host-owned catalog/provider snapshot, so repository-only profiles and repository overrides cannot add, rewrite, or reroute the selected auxiliary model. Candidate credentials require user-owned-or-higher secret authority; repository secret stores cannot satisfy or replace them. That profile owns candidate routing, context/reserve, hard output maximum, default reasoning, temperature, timeout, retry, sensitivity, and cost; it must support streaming and structured `Summary` work. The active main profile still owns pressure, emergency capacity, and the rebuilt ordinary request. Candidate calls advertise no tools and receive a required-first bounded task objective/acceptance intent plus source-preserving result projections. Task, prior-summary context, fact, and message projections all shrink under the candidate-profile capacity loop with explicit truncation/omission metadata. Every projected fact receives a short request-local host-issued id tied to one exact tool-result hash. The candidate returns only a bounded `factIds` array; it never supplies categories, cumulative headers, nested fact text, or source objects. The host discards unknown, repeated, and already-retained selections; greedily retains valid ids in model order only while the exact 32-item and rendered 4,096-token summary bounds fit; assigns deterministic allowed categories and cumulative metadata; and resolves each retained id to byte-exact content plus a deterministic attributable evidence/invocation source before the unchanged strict validator runs. A response with no usable ids is rejected. One malformed JSON selector response may consume the configured bounded retry. The host carries prior validated items unchanged; if cumulative bounds fill, it deterministically removes oldest prior items and records the cumulative pruned count in checkpoint/inspection metadata. Fabricated prose, cross-sibling source borrowing, altered prior facts, unknown sources, unsupported categories, oversized or unsanitized content, authority-bearing claims, and cumulative range/cut mismatches are rejected. Every unclassified tool-result group is conservatively repository-sensitive at the auxiliary boundary. Sensitive candidate input and request-specific cost-ceiling incompatibility are rejected during preflight when the configured candidate profile cannot receive them, before hooks or provider I/O. Preflight completes before lifecycle/accounting; every actual candidate attempt, including retries, crosses the managed model-request hook boundary and independently records reported-or-missing usage, call count, duration, and its true profile identity. Activated summaries are historical assistant evidence explicitly labeled as untrusted and non-authoritative; they never become durable conversation or repository memory automatically.

Interactive candidate execution temporarily projects `COMPACTING CONTEXT` with before/target tokens, candidate profile, and live elapsed duration. Completion emits one content-free line with the closed outcome plus actual model-visible before/after/savings/profile/duration and then resumes ordinary `THINKING`. The shared started/completed events contain no summary, rationale, source, prompt, or tool-result content; headless consumers receive those structured facts without terminal text.

A successful replacement increments a provider-neutral history generation. Compiled providers receive the complete rebuilt stateless request and therefore do not reuse an opaque response/conversation identity from the prior generation; the unchanged frozen prefix and canonical tools still permit safe provider prefix-cache reuse. Ordinary conversational runs keep the bounded summary checkpoint in memory, while original sanitized tool events and evidence remain durable under existing retention policy.

Cancellation and failed validation leave the original continuation active. Provider or insufficient-savings failures enter a two-pressure-round backoff. If the request still fits, the turn continues unchanged; if it does not, the deterministic compatibility reducer may shorten only older groups already delivered verbatim. A never-delivered group is not silently shortened and produces a controlled capacity failure when it cannot fit.

## Completed-turn compaction and failure behavior

Compaction runs only at a host-owned turn boundary. One operation per session may run at a time. The host bounds source messages, input tokens, candidate item count, item length, retries, and provider calls. Candidates remain untrusted until schema, sanitization, authority, provenance, evidence revision, supersession, and cycle validation pass.

The compacted-through sequence never advances beyond the exact retained source range. Cancellation, malformed output, unsupported provenance, provider failure, oversize oldest input, or persistence failure leaves the prior snapshot active. Automatic compaction failure is logged without failing an otherwise successful conversation.

Compaction never deletes archived messages. Full bodies have an independent retention age; ordered metadata, hashes, structured memory, and provenance remain restorable.

## Configuration

Configure repository overrides under `context:conversation` in `.threadsmith/config.*`. The complete schema is in `.threadsmith/config.example`.

To select an independent active-turn candidate model, set the following only in machine configuration, user configuration (`~/.threadsmith/config.json`), or the trusted `THREADSMITH_` environment layer:

```json
{
  "context": {
    "activeTurnCompaction": {
      "profileId": "00000000-0000-0000-0000-000000000000"
    }
  }
}
```

The GUID must identify one enabled profile in the repository-excluding immutable user/machine/host-owned provider catalog. The profile must support streaming and structured output and either declare `summary` in `intendedWorkloadClasses` or leave that list empty. Repository `config.json` values at this key are ignored, and repository provider-catalog additions/overrides are excluded from both candidate resolution and dispatch. Removing or setting the trusted value to `null` restores the active-main-profile fallback. Changing the compaction profile requires restart because provider catalogs and composition are immutable for the process lifetime.

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
| `context:repositoryMemory:maximumItems` | 12 |
| `context:repositoryMemory:maximumTokens` | 2,000 |

Invalid enum values, non-positive budgets, pressure outside 1–100%, retries exceeding the provider-call cap, malformed/missing/repository-only explicit compaction-profile IDs, statically incompatible profiles, runtime sensitive-data incompatibility, and request-specific cost incompatibility fail before model invocation. Active-turn pressure defaults are host-owned; only the trusted optional candidate profile ID is configurable.

## Retention and restoration

Persistence migration 2 owns archive, mode, memory, provenance-edge, and summary tables. Large sanitized bodies use the content-addressed artifact store and are hash-verified during restoration. Unknown future schemas and missing/corrupt artifacts produce bounded warnings rather than fabricated content.

`RetentionOptions.ConversationMessageBodyAge` defaults to 30 days independently of session-event retention. `RetainConversationBodies` can preserve full bodies. Structured memory and provenance remain cold durable state when bodies expire.

## Verification

Automated acceptance coverage is in `Threadsmith.ConversationContext.Tests`, including named Scenario I coverage for promotion, compaction, repository invalidation, all three modes, restart restoration, deterministic retrieval, prompt-injection escaping, pressure, cancellation/failure fallback, and TUI/headless command parity. Maintained terminal checks are in `docs/implementation-plans/manual-test-plan.md` §12.
