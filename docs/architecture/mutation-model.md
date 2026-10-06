# Mutation model

Source changes enter through the registered `edit_source` tool or the shared `ApplySourceEditCommand`. `SourceEditApplication` owns materialization, exact review, durable effects, semantic feedback and cumulative outcome recording. It invokes the existing `TransactionalWorkspaceCoordinator` and `TransactionalWorkspace`; there is one production writer.

## Instructions and authority

The caller supplies a rationale and ordered operation-specific instructions. The host supplies session/run/workspace identities, effect identity, current endpoint hashes, approval identity and validation policy. No plan membership grants write authority.

Supported instructions are create, exact text replacement, delete, move and supported C# symbol rename. Rename materializes into host-owned text patches through the existing semantic owner. Paths stay repository-relative and confined to approved roots. Secret paths, repository metadata, reparse points, trust and resource limits retain their existing enforcement.

A unique nonempty exact anchor can omit its offset. Empty insertions and repeated anchors require an exact offset. Unique line-ending differences are normalized without modifying text outside the matched region; ambiguous or different source is rejected. Ordered lifecycle operations use rolling candidate state while disk preconditions refer to the original touched endpoints.

## Authorization and transaction

Staging produces an exact diff. The existing mutation policy either requires review or auto-authorizes according to its supported rules. Partial authorization applies only the selected valid operations. Approval is bound to exact source state and cannot silently rebase after an external edit.

The effect journal records exact before/after identities before commit. The writer prepares temporary files, rechecks original identities, publishes final endpoints and verifies their bytes. Failure compensation restores only effects actually owned by that attempt and preserves unexpected external changes. Interrupted or mixed outcomes require recovery rather than an inferred success.

The latest retained committed edit remains rollback-capable through the same journal/writer. Rollback verifies current identities and cannot overwrite newer changes. Private staged state is discarded on denial, conflict or cancellation before application.

## Semantic feedback and completion

Compiler analysis is advisory and distinct from disk authorization. Syntax or semantic errors do not reject well-formed authorized edits. Candidate analysis is bounded and versioned; committed matching candidates reuse results. Broader analysis reports its coverage, pending state and omissions explicitly. Unknown document membership, project inputs and generator changes use the existing graph refresh owner.

The model receives an applied/conflict/denied/non-applied/recovery-required receipt and separate semantic evidence. Newer feedback enters the same conversation at a provider-compatible boundary without rewriting earlier tool results. A graph replacement retains delivery tracking while replacement analysis for the applied effect is pending. Missing or terminal obsolete analysis reports unavailable coverage.

User-visible feedback uses plain-text coverage and error summaries; structured analysis is model context. Error totals are omitted when coverage is pending, obsolete, or unavailable and must not be read as zero. Model instructions require repairing introduced errors within the requested scope or explaining a conflict with the request. The host neither waits for pending analysis nor enforces a compiler-clean completion gate.

Ordinary completion records cumulative disk effects without invoking validation. New feedback arriving during a final response permits a follow-up within the existing round and budget limits. Builds and tests run through explicitly invoked tools or validation commands; only their actual results establish validation. Supporting reads never expand write authority.

Historical plan DTOs, enum values and events exist solely to read supported stored history; they have no live execution producers. See [execution recovery](../operations/execution-resumption.md).
