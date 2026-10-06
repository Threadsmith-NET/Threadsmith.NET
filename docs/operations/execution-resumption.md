# Execution recovery

Direct source edits persist host-owned effect identities and exact original/final endpoint hashes through the existing execution checkpoint store and artifact publisher. Intent is durable before the writer starts; the proven disk outcome is retained after the write, including cancellation and failure paths.

Repeated invocation of the same effect with the same owning session, run, repository and request returns its recorded receipt. A different request cannot reuse that identity. Recovery compares current endpoints with the recorded snapshot instead of replaying the write:

- Exact final bytes establish an applied effect.
- Exact original bytes establish a non-applied effect.
- Mixed or unexpected bytes require explicit recovery and block further source edits in that repository.

Required snapshots and original-file artifacts survive retention while an effect is unresolved or its run outcome is unfinished. Cumulative diffs are published only when the effect history and current source identities establish that the changes belong to the run. External changes are preserved and reported rather than silently attributed to the model.

The latest retained committed edit can be rolled back through the same journal and transactional workspace. Rollback rechecks current identities immediately before destructive operations and refuses to overwrite newer changes.

## Historical sessions

Supported historical checkpoint schemas remain readable. Their plan, phase and event identities are compatibility data only. Reopening a historical session does not restore an executable planning workflow, approve changes, continue remaining steps or replay committed writes. Continue the objective through an ordinary conversation against current source.

Unresolved legacy write effects cannot safely be reconstructed from plan checkpoints alone. They cause a visible recovery warning and fence source editing until their state is explicitly resolved. Unknown or unreadable pending state fails closed. Compiler objects are reopened from the workspace; they are never restored from durable state.
