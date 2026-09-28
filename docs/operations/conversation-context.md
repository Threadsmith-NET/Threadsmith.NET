# Conversation Context Operations

Threadsmith preserves bounded cross-turn continuity without replaying an unbounded transcript. The host archives only sanitized accepted user requests and final visible assistant responses. When the host records a completed or failed execution outcome, it also archives one compact, sanitized host-authored assistant receipt with the original request, reported status, changed files, behavior summary, validation gate, rollback availability, and final diff reference. Its JSON text is historical data, never repository memory or instruction; a failed receipt can still list changed files, so its reported status remains authoritative. Recent exchanges pair requests and responses from the same run and are ordered by completion. New clones preserve these associations under fresh run IDs; older cloned archives retain their original adjacent pairs where run associations were not preserved. Hidden reasoning, provider payloads, raw tool output, and extension/provider types never enter durable conversation state.

## Modes

The compiled default is `ConversationAware`.

| Mode | Current turn | Recent complete turns | Explicit repository memories |
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
/context map
/context compact
```

`/context map` explains the latest actual prepared MAIN request using captured sizes. It opens a large modal with ordered source bars, aggregate category shares, and model-window usage. Double-click MAIN's top-right Context meter to open the same view, including during execution; mouse capture must be enabled (F12).

The view freezes when opened. Reopen it to see a later tool round or compacted request. It never assembles a preview, reloads instruction files, retrieves memories, or invokes a model. Old/resumed sessions can lack this transient detail until a new request is prepared. The captured model and window remain tied to that request even if you subsequently change settings. Submission is labeled only after the normal transport observer confirms it.

Headless integrations use the same host-owned contracts:

- `SetConversationContextModeCommand`
- `GetConversationStateCommand`
- `GetContextInspectionCommand`
- `RequestConversationCompactionCommand` (retired; returns actionable guidance)

`HeadlessShell.WriteContextInspectionAsync` emits the shared inspection projection as stable JSON.

## Repository-scoped memory

Memories are concise notes saved in the current repository's ignored `.threadsmith/threadsmith.db`. They survive `/new`, `/resume`, clone, and restart for that repository, and are shared by its local sessions. They are not tracked by Git or shared with other repositories.

Ask Threadsmith to remember a durable preference, correction, or project detail, and the model can call the single `memories` tool. You can also manage notes directly:

```text
/memory remember [--type standingPreference|situational] <text>
/memory list
/memory inspect <memory-id>
/memory update <memory-id> [--type standingPreference|situational] <replacement-text>
/memory forget <memory-id>
```

`supersede` is a compatibility alias for `update`: it now corrects the same ID rather than creating an inactive audit copy. `validate`, old category arguments, and validity filters are retired and return migration guidance. Both terminal frontends and headless commands use the same service. Terminal `/memory list` shows each note’s ID, origin, memory type, and text. `/memory inspect <id>` also shows creation/update times, inclusion count, last inclusion, and embedding identity or unavailability. Headless list output includes the entry metadata. Embedding vector components are excluded from headless JSON and terminal output.

New and updated text must fit `tools:config:memories:maximumTextCharacters` (default 2,000 characters) after sanitization and the local model's 256-token complete sequence limit, including boundary tokens. Shorten an oversized note; Threadsmith never saves full text with an embedding of only its beginning. Exact normalized duplicates return the existing ID, unchanged updates do nothing, and an update duplicating another note reports that note's ID without merging. If another caller changes a note while an update is being prepared, the update reports a conflict; inspect the current note and retry. Case and meaningful interior whitespace are preserved. Removing a note deletes current search/usage state; already transmitted context and historical conversation records remain historical.

Embedding upgrades preserve note IDs, text, content revisions, and usage history. On the next semantic lookup, vectors from an older embedding space are rebuilt locally against the current text. Notes that no longer fit the encoder remain inspectable and lexically searchable; shorten them with `update` to restore semantic search. The current encoder counts four boundary tokens within its 256-token limit, leaving up to 252 content tokens.

By default, at most twenty notes are stored across both types, and zero to three relevant situational notes enter each request in addition to all standing preferences. Retrieval combines SQLite lexical matches and local semantic similarity, then applies mode, sensitivity, and token budgets. Unrelated notes need not appear. `/context inspect` separates selection and final budget inclusion from actual-submission receipt outcomes, and reports branch contributions, query truncation, cache reuse, and fallback. Listing or previewing context does not count as inclusion; repeated tool rounds count once per user turn/content revision. Receipt retention follows the run’s recorded repository database bindings, including a repository switch during the run, so pruning an old run also removes its bound receipt records.

Memories are either **situational** or **standing preferences**. New and older unspecified memories are situational; an update without `--type` preserves its existing type. Situational memory is best-effort recall. Standing preferences are included on every non-`Stateless` request while memory is enabled: they bypass hybrid retrieval, reranking, and the situational count cap, reserve required context, and are never silently dropped for token pressure. If they cannot fit, the request fails normally for insufficient capacity. At capacity, older/disused notes may be evicted, with the same policy for manual and model notes. Meaningful adds/corrections receive a seven-day recency window when older candidates exist; if all notes are new, the oldest can still be evicted. Put instructions that must always apply in `AGENTS.md`. Routine conversation, approvals, mutations, rollback, and completion do not automatically create notes, and repository edits do not automatically invalidate them.

Configure these settings through ordinary machine, user, repository, session, CLI, and `THREADSMITH_` environment layering:

```json
{
  "tools": {
    "config": {
      "memories": {
        "MaxNumberOfRepoMemories": 20,
        "MaxRepoMemoriesInContext": 3,
        "SemanticMinimum": 0.47,
        "RerankerEnabled": false,
        "RerankerCandidateLimit": 8,
        "RerankerMinimumScore": null,
        "standingPreferenceWarningThreshold": 3
      }
    }
  }
}
```

Storage capacity must be positive; the situational context limit may be zero to disable situational retrieval and cannot effectively exceed storage capacity. A lower capacity is enforced only when the next repository bind/configuration refresh succeeds. Failed repository opens preserve the target repository's stored memories and pending memory migration. `SemanticMinimum` must be a finite double in `[-1, 1]`. Raising it makes semantic retrieval more selective; lowering it permits weaker semantic matches. At `1`, no semantic candidate can pass the strict comparison, while lexical retrieval still works. Memory configuration is captured per operation and user turn when the repository is bound. Threadsmith does not watch configuration files for live reload: restart or reopen the repository after a file edit. A threshold-only change reranks cached retrieval for the next request while reusing query vectors; it does not rebuild note vectors or change `SpaceId`. Tool enable/deny controls withhold model operations and automatic memory injection together; explicit manual management remains available. Old `context:repositoryMemory` settings are ignored with a deprecation diagnostic.

Set `tools:config:memories:RerankerEnabled` to `true` to enable optional local cross-encoder reranking after hybrid lexical/semantic qualification and before the final context cap. It is disabled by default. `RerankerCandidateLimit` defaults to 8 and accepts any positive count; a smaller pool can return fewer memories than the context maximum. A successful score uses a raw relevance logit: higher ranks first, with hybrid score and stable ID breaking ties. `RerankerMinimumScore` defaults to `null` (no rejection); an explicitly supplied finite value requires a score strictly above it. This is a separate scale from cosine similarity. A repository JSON `null` clears an inherited reranker cutoff. `standingPreferenceWarningThreshold` defaults to 3 and accepts nonnegative values. At startup, after a successful manual or model addition, or after an update explicitly selecting standingPreference, following any eviction, Threadsmith warns only when the standing-preference count is greater than that threshold: `You now have {n} preference memories. You may want to consider adding some of these to AGENTS.md for the repo.` No rejection default has been calibrated for repository memories.

`reranking:cpuThreads` is a startup-only setting, default 8, any positive count. Restart to change it. The model is loaded lazily and reused; disabling reranking or having no candidates avoids inference. Source users run `eng/Stage-RerankerAssets.ps1` to stage the pinned model and vocabulary; releases bundle them. Runtime performs no downloads. Missing assets, inference errors, invalid scores or any truncated query-memory pair retain the original hybrid selection and produce diagnostics. Failed reranking is cached only within an identified user turn and retried later. The reranker does not rewrite the query or memories.

Included situational memories carry the caption **Repository memories that may be helpful**, followed by guidance to use them only when relevant. Standing preferences have a separate **Standing preferences** caption and guidance that they apply across requests. Both remain untrusted reference data. Standing preferences bypass the 2,000-token situational framing budget; all content and captions still count toward the model input capacity.

The bundled CPU encoder works locally and independently of the conversational model. If it is unavailable, writes that change text fail visibly and situational retrieval falls back to qualified lexical matches. Type-only updates reuse the stored vector. A failed search index preserves readable standing preferences; if the memory database cannot be read, context assembly fails with an explicit error. Imported older manual notes remain inspectable even when too long for the encoder and can be corrected with `update`. See [conversation context operations](conversation-context.md) for migration backups and recovery.

## Inspection

`/context inspect` reports:

- effective mode and configuration/session source;
- current archived message identity;
- empty legacy automatic-summary fields, never restored as prompt content;
- included and omitted recent messages;
- selected, final included, and budget-excluded repository-memory IDs/content revisions;
- actual-submission receipt outcome and elapsed accounting time, separate from preview/assembly;
- source message, run, and evidence identifiers;
- lexical/semantic ranks, cosine/fusion scores, optional cross-encoder scores, query truncation, model space, cache reuse, rebuild/fallback rationale;
- category token accounting and exact pressure reductions;
- context-window pressure and the assembly pressure indication;
- the latest active-turn pre-sampling estimate, context/budget pressure reason, main-profile output allowance, configured/effective retention, candidate profile identity, combined summary-and-continuation admission, exhausted dimensions, eligible/compacted/retained group counts and tokens, summary/pruned/history generation, cut range, backoff, and classified outcome;
- the before/after complete-request token estimates and active-turn outcome. The shared headless inspection projection additionally carries exact-source candidate, removed, retained, and opaque counts; source characters reclaimed before receipt overhead; and whether projection avoided a summary.

Inspection contains metadata, bounded sanitized memory content only in the assembled prompt, and no secret/provider/tool payloads.

## Active-turn tool continuation compaction

Every tool round adds calls and results to the active conversation. Without compaction, each later model request carries those results again. Repeated source reads can therefore make requests grow even when they add little new information. Threadsmith manages that growth in two stages: it first removes source text that is provably duplicated by newer active context, then uses a model-written summary only when deterministic removal does not resolve the pressure.

### Three representations with different purposes

| Representation | Purpose | Changed by source projection? |
|---|---|---:|
| Stored evidence and tool results | Audit, recovery, and durable execution records | No |
| Active chronological call/result groups | Dependency and lifecycle state for the current run | No |
| Prepared model request | The exact messages and schemas serialized for the next provider call | Yes |

Source projection is a request-time view. It does not delete or rewrite the original result. The model receives a smaller request, while the host retains the admitted sanitized evidence needed for bounded recovery.

### Request lifecycle

For every ordinary multi-round evidence or planning request, Threadsmith:

1. Builds the complete provider request, including instructions, messages, tool schemas, and provider framing, while reserving the selected profile's required output capacity.
2. Estimates that exact prepared input with the canonical provider-wire estimator and checks it against the remaining input capacity and execution budget.
3. Sends it unchanged when neither the context-pressure threshold nor the remaining execution budget requires reduction.
4. Under pressure, checks already delivered `read_file` and `code_explore` results for exact source ranges that a newer result fully covers.
5. Replaces eligible older source bodies with compact receipts and rebuilds the complete request, including the conditional recovery-tool schema.
6. Activates the projection only when the rebuilt request has positive net token savings and passes the pressure and capacity checks. If projection saves tokens but cannot resolve the pressure alone, the existing summary fallback evaluates a dependency-safe projected prefix.
7. Validates the final request immediately before dispatch. A receipt cannot survive without its supporting newer source and authorized evidence.

A newly completed tool group must first reach the model exactly in a later completed request before it can become eligible. Current user input, host and repository instructions, output contracts, canonical tool definitions, and the initially assembled prefix remain unchanged.

### Concrete example

Suppose one tool result contains `Parser.cs` lines 1–400 and a later result contains lines 1–500 from the same file snapshot. The ordinary history would resend both source bodies:

```text
read_file Parser.cs lines 1-400
[400 lines of source]

read_file Parser.cs lines 1-500
[500 lines of source]
```

Under pressure, the prepared request can instead contain:

```text
read_file Parser.cs lines 1-400
[receipt: exact source is covered by the later result; original evidence is recoverable]

read_file Parser.cs lines 1-500
[500 lines of source]
```

The model still sees the newer complete source range and the fact that the older call occurred. The repeated 400-line body is absent from the serialized request. With a chain such as A covered by B and B covered by C, older receipts point directly to the newest surviving result rather than forming a recovery chain.

### Exact proof and fail-closed behavior

Threadsmith removes an older range only when all applicable facts match:

- the older and newer results came from the same supported producer: `read_file` with `read_file`, or `code_explore` with `code_explore`;
- both identify the same normalized file path and raw-file SHA-256 snapshot;
- the newer range is equal to or wholly contains the older range;
- the exact sanitized lines previously delivered to the model match the recorded visible-range digest;
- `code_explore` results also have the same workspace generation; and
- the supporting newer source will remain model-visible in the final request.

Partial overlap, cross-tool overlap, changed or missing digests, different workspace generations, hidden-only sidecars, malformed metadata, unsupported producers, never-delivered results, and candidates outside the bounded 256-item assessment remain unchanged. Whole source fragments are removed; Threadsmith does not split a result to remove only its overlapping subsection.

### Recovery

When the final request contains receipts, Threadsmith dynamically advertises `read_active_turn_evidence`. The tool returns bounded pages from the original sanitized evidence. Authorization is tied to the same session, run, repository identity, evidence record, and producing tool invocation. It uses the ordinary tool pipeline, so normal policy, events, cancellation, paging, and execution accounting apply.

Recovery never rereads the current repository file. A current reread could return different content and would not prove what the model originally saw. Missing, stale, or unauthorized evidence produces an explicit result. Summarizing a receipt-bearing raw group revokes its recovery reference, and final validation rejects any projection that would leave a receipt without its supporting source. A model-initiated recovery can add a tool round; projection itself adds no provider call.

### Token and transport efficiency

| Layer | Effect |
|---|---|
| Model input | Repeated source tokens are removed from the complete prepared request. The before/after estimate includes receipt text, the recovery schema, and message framing; admission separately preserves the selected profile's output reserve. Reported savings are therefore net input-request savings rather than raw text estimates. |
| Network transport | Removed source bodies are not serialized into the provider request. The request therefore carries fewer characters and normally fewer bytes as well as fewer tokens. This is omission at request construction, not compression applied after serialization. |
| Provider calls | Projection requires no model call. It can avoid a summary-provider call when exact removal resolves pressure. Recovery adds a normal tool round only if the model requests the omitted body. |
| Host storage | Original sanitized tool results and evidence remain stored under existing retention rules. The optimization reduces provider-bound context, not durable evidence size. |
| Provider caching and billing | Threadsmith advances `HistoryRewriteGeneration` so providers cannot reuse an incompatible opaque conversation identity. The unchanged prefix can still qualify for provider prefix caching, but cache accounting, billed tokens, latency, and byte encoding remain provider-specific. |

Small duplicate results can cost more to describe than they save because a receipt and recovery schema have fixed overhead. Threadsmith compares the complete before/after requests and keeps the raw result when net savings are zero or negative. Workloads dominated by unique reads, partial overlaps, or rapidly changing files should therefore show little or no source-projection benefit.

The deterministic high-overlap evaluation replaced 36,615 repeated source characters. The third request fell from an estimated 22,161 input tokens to 13,098, a 40.9% reduction; cumulative estimated input across three requests fell from 37,930 to 28,867, a 23.9% reduction. Model-request count stayed at three and no summary call was needed. These figures include receipt and recovery-schema overhead and demonstrate the mechanism under deliberate overlap; they are not a general workload or billing forecast. See the [Plan 113.2 evaluation](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/evidence/plan-113.2-deduplication-evaluation.md) for the fixture, controls, and limits.

### Summary fallback and execution budgets

Execution budgets are scoped per run by production composition and by the default concrete `ExecutionBudget` path. This keeps concurrent runs from racing a shared admission snapshot. Callers that provide a custom budget factory own any intentional cross-run sharing. Within one run, ordinary and summary provider requests remain serial, and actual reported usage is accrued before the next admission check.

At 75% of the active main profile's effective input budget, after honoring that profile's effective request output reserve, the host may replace an older delivered prefix with one validated cumulative summary. The same path is assessed when the prepared ordinary request cannot fit the remaining execution budget. The configured 12,000-token newest-raw target stays independent of the pressure target, frozen request cost, and summary allowance. Retention rounds up to complete groups, or keeps all available groups when there is less history; at least the newest complete group remains exact. Actual input-capacity checks and emergency reduction remain separate. Activation requires positive estimated savings in the rebuilt request, but it does not require one compaction to fall below the pressure target. Candidate input is bounded by both the 65,536-token host ceiling and the candidate profile's context window minus its effective output reserve, with one global projection budget across at most 48 source groups and 512 call/result messages. Candidate output is requested using 80% of the summary allowance by default: 13,107 output tokens for the default 16,384-token allowance, subject to the provider's supported controls. Completed rendered summaries have a separate 65,536-character host bound used to derive conservative continuation admission. Provider stream controls, normal-completion checks, and rebuilt-request savings and capacity checks remain separate. When no separate candidate profile is configured, the active main profile remains the fallback. Repository content cannot disable, delay, or lower the pressure boundary.

An optional repository-excluding trusted setting may route candidate calls to a different configured model profile or provider. Resolution and dispatch use a repository-excluding user/machine/host-owned catalog/provider snapshot, so repository-only profiles and repository overrides cannot add, rewrite, or reroute the selected auxiliary model. Candidate credentials require user-owned-or-higher secret authority; repository secret stores cannot satisfy or replace them. That profile owns candidate routing, context/reserve, hard output maximum, default reasoning, temperature, timeout, retry, sensitivity, and cost; it must support streaming `Summary` work. The active main profile still owns pressure, emergency capacity, and the rebuilt ordinary request. Candidate calls advertise no tools and receive a required-first bounded task objective/acceptance intent, the complete prior active-turn summary when present, the newly old selected raw tool activity, and host-observed file lists. The model returns one completed Markdown checkpoint. The host validates schema, exact cumulative covered group range, host file lists, sanitization, authority markers, and rendered size; strips any model-emitted `Files read` or `Files changed` sections; and appends host-owned cumulative file lists. Every unclassified tool-result group is conservatively repository-sensitive at the auxiliary boundary. Sensitive candidate input and request-specific cost-ceiling incompatibility are rejected during preflight when the configured candidate profile cannot receive them, before hooks or provider I/O. Before each actual attempt and retry, the host admits the prepared summary request together with a provider-prepared worst-case continuation for that attempt's exact prefix. The combined check includes input, output that can physically fit in the selected context window under the provider's enforced request ceiling or hard profile maximum, two calls, and reviewed cost. Unknown pricing remains unknown and cannot enforce a cost limit; provider timeouts are not treated as whole-operation duration bounds. Rejected attempts invoke neither hooks nor providers and accrue no call. Actual reported usage remains authoritative and is accrued once. The rebuilt ordinary request is checked again after summary usage accrues. Every dispatched candidate attempt crosses the managed model-request hook boundary and independently records reported-or-missing usage, call count, duration, and its true profile identity. Activated summaries are historical assistant evidence explicitly labeled as untrusted and non-authoritative; they never become durable conversation or repository memory automatically.

Interactive candidate execution temporarily projects `COMPACTING CONTEXT` with before/target tokens, candidate profile, and live elapsed duration. Completion emits one content-free line with the closed outcome plus actual model-visible before/after/savings/profile/duration and then resumes ordinary `THINKING`. The shared started/completed events contain no summary, rationale, source, prompt, or tool-result content; headless consumers receive those structured facts without terminal text.

A successful replacement increments a provider-neutral history generation. Compiled providers receive the complete rebuilt stateless request and therefore do not reuse an opaque response/conversation identity from the prior generation; the unchanged frozen prefix and canonical tools still permit safe provider prefix-cache reuse. Ordinary conversational runs keep the bounded summary checkpoint in memory, while original sanitized tool events and evidence remain durable under existing retention policy.

Cancellation and failed validation leave the original continuation active. Provider failures or zero-or-negative-savings candidates enter a two-pressure-round backoff. An ordinary request that cannot fit and has no feasible summary-plus-continuation path fails with controlled budget exhaustion before provider dispatch. If the request still fits, the turn continues unchanged; if it does not fit context capacity, the deterministic compatibility reducer may shorten only older groups already delivered verbatim. A never-delivered group is not silently shortened and produces a controlled capacity failure when it cannot fit.

## Historical automatic memory and failure behavior

`/context compact` no longer promotes completed turns into structured facts. Historical automatic items/snapshots may remain readable as archive metadata but never feed current prompt restoration, including resume or clone. Ordinary conversation messages and model-generated active-turn compaction remain separate context sources.

Evidence storage sanitizes structured JSON value-by-value, preserving valid syntax and retaining original formatting when no redaction occurs. Non-JSON evidence retains ordinary text sanitization. Situational search failures omit situational matches while preserving readable standing preferences. If the underlying memory snapshot cannot be read, assembly fails explicitly because required preferences cannot be recovered; cancellation still cancels the request. Receipt-accounting failure is diagnosed without a second model submission.

## Configuration

Configure repository overrides under `context:conversation` in `.threadsmith/config.*`. The complete schema is in `.threadsmith/config.example`.

To select one active-turn compaction model for the main loop and all subagent roles, set the following only in machine configuration, user configuration (`~/.threadsmith/config.json`), or the trusted `THREADSMITH_` environment layer:

```json
{
  "context": {
    "activeTurnCompaction": {
      "profileId": "00000000-0000-0000-0000-000000000000",
      "reasoningLevel": "medium",
      "summaryBudgetTokens": 16384,
      "modelOutputBudgetPercent": 80
    }
  }
}
```

The GUID must identify one enabled profile in the repository-excluding immutable user/machine/host-owned provider catalog. The profile must support streaming and either declare `summary` in `intendedWorkloadClasses` or leave that list empty. Optional `reasoningLevel` defaults to the profile setting; an explicit value must be supported. Both main and child summaries use this model and reasoning, without changing their task models or pressure triggers. Repository `config.json` values at this path are ignored, and repository provider-catalog additions/overrides are excluded from candidate resolution, provider instructions, and dispatch. Remove or set both model fields to `null` to restore each loop's own active model and reasoning; a reasoning override without a profile fails startup. Trusted main-loop `summaryBudgetTokens` and `modelOutputBudgetPercent` can also be set at this path; subagent summary budgets remain under `agents:delegation:compaction:summary`. Changing these settings requires restart because provider catalogs and composition are immutable for the process lifetime.

Compiled defaults:

| Setting | Default |
|---|---:|
| `mode` | `ConversationAware` |
| `recentTurnTokens` | 8,000 |
| `recentTurnCount` | 12 |
| `recentTurnMaximumAge` | 7 days |
| `compactionPressurePercent` | 75 |
| `artifactThresholdCharacters` | 16,384 |
| `tools:config:memories:MaxNumberOfRepoMemories` | 20 |
| `tools:config:memories:MaxRepoMemoriesInContext` | 3 |
| `tools:config:memories:SemanticMinimum` | 0.47; finite `[-1, 1]`, with strict semantic score comparison |
| `tools:config:memories:RerankerEnabled` | `false` |
| `tools:config:memories:RerankerCandidateLimit` | 8; any positive count |
| `tools:config:memories:RerankerMinimumScore` | `null`; optional finite raw-logit strict minimum |
| `tools:config:memories:standingPreferenceWarningThreshold` | 3; nonnegative, warns when count is greater |
| `reranking:cpuThreads` | 8; startup-only, any positive count |
| `activeTurnCompaction.summaryBudgetTokens` | 16,384 trusted-only |
| `activeTurnCompaction.modelOutputBudgetPercent` | 80 trusted-only |

Invalid enum values, non-positive budgets, pressure outside 1–100%, malformed/missing/repository-only explicit compaction-profile IDs, statically incompatible profiles, runtime sensitive-data incompatibility, and request-specific cost incompatibility fail before model invocation. Active-turn pressure defaults remain host-owned; the optional candidate profile ID and summary budget partition are configurable only through trusted machine/user/environment configuration.

`context:conversation:compactionPressurePercent` controls the context assembler's recommendation for bounded cross-turn conversation history. It does not change the active-turn tool-continuation trigger described above. Active-turn source projection and summary assessment use the compiled host-owned 75% trigger; `context:activeTurnCompaction` configures only the optional summary profile, reasoning, summary budget, and output partition.

## Retention and restoration

Persistence migration 2 owns archive, mode, memory, provenance-edge, and summary tables. Large sanitized bodies use the content-addressed artifact store and are hash-verified during restoration. Unknown future schemas and missing/corrupt artifacts produce bounded warnings rather than fabricated content.

`RetentionOptions.ConversationMessageBodyAge` defaults to 30 days independently of session-event retention. `RetainConversationBodies` can preserve full bodies. Explicit repository memories follow their own capacity/eviction rules; retired automatic snapshots are never restored to prompts.

## Migration and backup recovery

The forward memory migration creates a SQLite-consistent, integrity-checked sibling backup before changing a file-backed database. Its name begins `threadsmith.db.pre-managed-memory-v10.` and ends `.backup`; the exact path is recorded in migration diagnostics/metadata. This uses SQLite's backup API and includes committed WAL contents; do not copy a live WAL database as a plain file.

Only active legacy entries with explicit manual/user-command provenance are imported. Their IDs, text, and timestamps are preserved where possible; usage starts at zero, capacity is enforced, automatic/inactive entries are excluded, and old repository-memory tables are retired. Oversized imported text stays inspectable and lexically searchable within context bounds; use `update` to make it encodable. Compatible vectors rebuild outside the migration transaction against unchanged content revisions.

For a deliberate rollback, stop every Threadsmith process using that repository. Preserve the newer database with a fresh SQLite backup first. Verify the recorded pre-migration backup with SQLite `PRAGMA integrity_check`, then restore it with SQLite's backup/restore API into the intended database while no application is connected. Keep the preserved newer database and its backup until verification completes. Restoring intentionally loses all changes since that backup; checking out older Git code alone never downgrades a database, and Threadsmith does not automatically overwrite a newer one.

## Verification

Automated acceptance coverage is in `Threadsmith.ConversationContext.Tests`, including explicit CRUD, complete-input bounds, lexical/semantic qualification, idempotent receipts, all three modes, legacy snapshot exclusion, restart restoration, prompt-injection escaping, pressure, cancellation/failure fallback, and TUI/headless command parity. Maintained terminal checks are in the source-repository [manual test plan](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/manual-test-plan.md).
