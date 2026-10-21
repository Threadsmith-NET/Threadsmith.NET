# Memory and intelligence routing

Standing preferences remain required, query-independent context in ID order. They do not use situational result limits, concept eligibility, cross-encoder slots, or the situational token budget. Memory-disabled and stateless runs keep their existing gates.

## Settings and rollback

Settings live under `tools.config.memories` in `.threadsmith/config.json`. Merge these fields into existing configuration; do not replace other settings.

```json
{
  "tools": {
    "config": {
      "memories": {
        "Recall": {
          "SituationalConceptFilterMode": "Off"
        },
        "Intelligence": {
          "Enabled": true,
          "AutomaticRecallEnabled": false
        }
      }
    }
  }
}
```

This is the explicit **on-demand** choice. Reopen the repository after editing configuration. The master default remains disabled; this example explicitly enables it. Enabling the master resumes legacy monitoring and configured collection consumers for onboarded repositories. It does not initiate onboarding. Changing automatic recall alone does not initiate collection, onboarding, or archeology.

| Master / monitoring | Automatic recall | Automatic intelligence | `repository_intelligence` |
|---|---|---|---|
| Disabled | Any | No | `disabled` |
| Enabled, not onboarded | Any | No | `not_collected` |
| Enabled, monitoring fault | Any | No | `error` |
| Enabled and effective | Missing or true | Legacy behavior | Available |
| Enabled and effective | False | No search or injection | Available |

Existing configurations with no automatic-recall key retain legacy behavior. Onboarding's existing enable-before-publication transaction preserves an explicit false automatic-recall setting. To roll back on-demand routing, set `AutomaticRecallEnabled` to true. No memories, embeddings, tags, receipts, or embedding spaces are migrated.

`Recall.SituationalConceptFilterMode` accepts `Off`, `Shadow`, and `Enforce`. Off is the default. Invalid values resolve to Off with a configuration diagnostic included in recall diagnostics. `Recall.Concepts.Enabled` still controls candidate expansion independently.

- **Off:** baseline eligibility.
- **Shadow:** same candidates, scores, comparisons and submitted memory text as Off; reports hypothetical eligibility decisions.
- **Enforce:** discovered tagged ordinary situational candidates need an exact normalized current-instruction concept match. Untagged candidates retain baseline eligibility. Missing, stale, cross-run, cross-repository, overflowed or mixed-origin evidence falls back to baseline.

Hints are admitted native-tool metadata, not semantic interpretations of user messages. They become available after a complete tool batch, so the initial round normally has none. Admitted failed tools may supply hints; denied and invalid calls do not. Steering and repository changes advance the epoch and clear filtering evidence. Late batches cannot restore an old epoch. Different hint sets from multiple tool invocations are treated conservatively as mixed origins. Existing discovery accumulation is separate and unchanged. Shadow collects evidence even with discovery disabled, but evaluates candidates only when baseline recall would assemble context; it does not create extra retrieval or provider-replay work solely for instrumentation. Structured debug observations expose admitted hint counts, epoch, round and outcome without tool arguments.

An exact match does not bypass relevance, complete comparison, ranking, retention revalidation or context budgets. Spelling correction can discover candidates but cannot satisfy the hard gate. Reconciliation and intelligence search never inherit this policy. Roll back filtering by selecting Off.

## Explicit search

Use `repository_intelligence`:

```json
{"query":"Why are extension contracts isolated from host runtime types?"}
```

Search requires the existing tool policy, master enablement and effective monitoring. It cannot select another repository or ownership partition. Query length is 1–8,000 characters; results use `Intelligence.Recall.MaximumResults` capped at 20 and the serialized-entry budget `MaximumListBytes`. The shared tool output limit still applies to the complete response. Entries include IDs, text, revision, ownership, concepts, supports, advisory freshness and untrusted status. No vectors are returned.

Outcomes distinguish `matches`, `no_matches`, `disabled`, `not_collected`, `incomplete`, and `error`. `IsComplete`, `QueryTruncated`, `OmittedEntries` and search details describe separate completeness/output limits. An empty result does not start collection. Supports identify evidence leads, not verified present-day applicability. Archeology can query stored intelligence through the same read-only service. Ordinary memory actions remain separate.

Search reuses the hybrid backend. It disables that backend's embedding-rebuild writes. The backend still reads the repository snapshot and performs existing lexical/vector work; this is not a constant-cost indexed catalog. Existing list behavior is unchanged and loads a snapshot before bounding output.

## Evaluation and activation

Filtering is implemented but has not earned broad activation. No captured runtime concept-supply dataset was supplied with this change. Real useful-memory recall, irrelevant injections, contaminated turns, correct empty turns, missed-useful empty turns, failure rates, injected tokens, latency, coverage by round, provenance validity, tagged coverage, exact/typo rates, activation, untagged share and false exclusions are **N/A (0 evaluated captured turns)**. Deterministic branch tests do not establish real intent coverage.

Before any promotion, freeze family-separated calibration and held-out judgments, acceptable useful-recall loss and latency bounds. Replay identical inputs through actual context assembly, separately report candidate and final useful-memory recall, and keep standing preferences outside retrieval-quality metrics. Report numerators/denominators for all turns and applicable-policy turns, retain counterexamples, and review recall tradeoffs explicitly. Do not tune on the held-out split. Keep content-bearing material local and authorized.

The Memory browser supports Insert to add, Enter/F4 to edit, and Delete to remove on each tab. Add opens the shared text editor; Save Changes stores a standing preference on Preferences, an ordinary situational memory on Situational, or an intelligence-owned situational memory on Intelligence. Escape or Cancel discards the draft. Blank drafts cannot be saved, and failed saves preserve the draft.

Manually added intelligence is marked with manual origin and does not claim observed file supports. Model-generated and collection intelligence still require primary-source supports. Capacity, reconciliation, and recall policies remain in effect for manual additions.

## Focused historical investigation

`repository_archeology` accepts `{"question":"Why did this subsystem change?"}` (1–2,048 characters). It starts exactly one read-only Explorer through the existing delegation scheduler. The child uses current source, stored intelligence and local Git history. History readers may be temporarily available even when disabled for ordinary requests; explicit denials and repository path policy still apply. Nested investigations and delegation, mutation, collection and network access are unavailable.

Both repository tools remain in the Tools catalog with a readiness reason. `/intelligence enable|disable` and either tool row control the same feature setting. Enabling never onboards or changes automatic recall. Ordinary `memories` enablement is independent. Existing per-tool denials are not removed by the shared toggle.

Archeology carries an absolute deadline from pipeline admission through scheduler/source waiting and child execution. Expired queued calls return a correlated timeout without starting a child. Archeology enforces ceilings of 120 seconds, 8 model rounds, 24 tool calls and 16,000 cumulative model tokens, with stricter inherited budgets preserved. Archeology does not run additional summary-model compaction calls outside that model-round ceiling. Shared disable, monitor failure, repository rebind and cancellation join the child before releasing the exclusive intelligence operation. Stored queries can remain available while an investigation is busy. Partial evidence is not proof that no relevant history exists.

Historical `memories` search calls receive `legacy_search_removed` with migration guidance. The old query/ownership parameters are no longer advertised. Completed historical results remain readable.
