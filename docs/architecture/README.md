# Architecture Reference

Architecture decision records (ADRs) explain why Threadsmith owns trust, policy, context, mutation, validation, integration, and release boundaries. An ADR can retain historical milestone or plan terminology; use the [user guide](../user-guide.md) and [operations reference](../operations/README.md) for current user-facing behavior.

Subsystem summaries:

- [Context policy](context-policy.md)
- [Event catalog](event-catalog.md)
- [Mutation model](mutation-model.md)
- [Semantic confidence](semantic-confidence.md) and [semantic mutations](semantic-mutations.md)
- [Test selection](test-selection.md) and [validation pipeline](validation-pipeline.md)

Key governing decisions include [central tool policy](adr-11-central-tool-policy-pipeline.md), [typed transactional mutations](adr-13-typed-transactional-mutations.md), [governed declarative skills](adr-34-governed-declarative-skills.md), and [canonical release payloads](adr-37-canonical-release-payload-and-installers.md).
