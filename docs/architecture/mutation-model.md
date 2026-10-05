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

The model receives an applied/conflict/denied/non-applied/recovery-required receipt and separate semantic evidence. Final acceptance uses the configured validation pipeline over cumulative affected scope. Supporting reads never expand write authority.

Historical plan DTOs, enum values and events exist solely to read supported stored history; they have no live execution producers. See [execution recovery](../operations/execution-resumption.md).
