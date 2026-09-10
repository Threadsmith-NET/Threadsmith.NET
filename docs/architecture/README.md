# Architecture Reference

Architecture decision records (ADRs) explain why Threadsmith owns trust, policy, context, mutation, validation, integration, and release boundaries. An ADR can retain historical milestone or plan terminology; use the [user guide](../user-guide.md) and [operations reference](../operations/README.md) for current user-facing behavior.

Subsystem summaries:

- [Context policy](context-policy.md)
- [Event catalog](event-catalog.md)
- [Mutation model](mutation-model.md)
- [Semantic confidence](semantic-confidence.md) and [semantic mutations](semantic-mutations.md)
- [Test selection](test-selection.md) and [validation pipeline](validation-pipeline.md)

Key governing decisions include [central tool policy](adr-11-central-tool-policy-pipeline.md), [typed transactional mutations](adr-13-typed-transactional-mutations.md), [governed declarative skills](adr-34-governed-declarative-skills.md), and [canonical release payloads](adr-37-canonical-release-payload-and-installers.md).

[ADR-54](adr-54-direct-fetch-approval-durations.md) defines one-attempt, live-session, and saved user hostname approval choices.

[ADR-53](adr-53-search-result-host-authorization.md) defines current-run hostname authorization from eligible search results and its relationship to exact URL grants.

[ADR-55](adr-55-direct-artifact-file-writes.md) defines direct allowlisted report/data writes and exact saving of prior assistant answers.

[ADR-56](adr-56-repository-only-approval-policy-preferences.md) defines repository-only persistence for plan and mutation policy selections, with session-only overrides.

[ADR-59](adr-59-model-managed-repository-memories.md) defines explicit repository-memory operations, local embeddings, hybrid retrieval, inclusion accounting, and deterministic capacity eviction.

[ADR-60](adr-60-native-anthropic-provider-and-transient-replay.md) defines native Anthropic discovery, request preparation, independent thinking controls, and private signed tool continuations.
