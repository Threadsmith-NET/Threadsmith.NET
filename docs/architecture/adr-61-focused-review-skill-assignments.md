# ADR-61 — Explicit focused review skill assignments

Status: Accepted

## Decision

An explicit, verified invocation of the shipped public `review` workflow is an additional narrow child-launch entry to ADR-57. Its `request-review` step must match the compiled recipe, exact public manifest and deployed location. Ordinary `delegate_agents` retains its model-origin and exact tool-snapshot guards. No other workflow action, role, task text, restored session or package named `review` grants this entry.

Four assignment-only native packages ship under `ReviewSkills`, outside the public catalogs. Native parsing, confinement, content hashing, trust/revocation and schema validation remain authoritative. A compiled recipe pins their manifests, procedures and assets. App composes Skills and Execution through Core contracts; neither implementation depends on the other.

Focused children use the existing role/model selection, scheduler and child model loop. Only a valid host binding adds private procedure content and an optional completion policy. Ordinary responses, including empty strings, retain `agent-response/1` semantics. Each focused context starts from the captured target, explicit instructions and requirements, with no parent evidence or conversation. A confined snapshot reader replaces live inspection; reviewers cannot execute processes or access a network. Git acquisition is a separate typed host operation.

The target captures committed and dirty local content or isolated remote Git objects. Without a remote base, review is a snapshot audit. Requirements default to the invoking workspace and retain every explicit criterion. Static review cannot attest runtime/manual acceptance. Deterministic synthesis preserves validated results and failures; a dedicated formatter owns the fixed Markdown layout.

Versioned review records reside in host-managed review storage, separately from public skill checkpoints. Launch identities are written before dispatch. Interrupted inference is never restarted on resume; durable completed children can be joined and delivery retried without model calls. No exactly-once inference guarantee is made. Reports publish atomically into an existing authorized invoking-repository `.inbox`, or use canonical console output when absent. This does not authorize directory creation, code changes or remote publication.

## Consequences

Native/Claude skills and ordinary role replies keep their prior contracts. Private definitions never enter public discovery or joined results. Schema and citation validation establish structure and provenance, not correctness. Partial coverage remains explicit. Broader CI, PR-provider, SARIF and finding-lifecycle work remains owned by Plan 60.
