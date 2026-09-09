# ADR-58: Current mutation baselines and text anchors

Status: Accepted

## Context

The one-line ShellRunner repeat test reused a mutation snapshot captured before an external rollback. Read evidence and the mutation baseline therefore described different bytes. The proposal used valid CRLF line breaks, already handled by logical-newline matching, but the old baseline still contained the prior test field. Reconstructing that field in the rolled-back source reproduces the logged baseline SHA-256 exactly. The model also had to produce mandatory numeric offsets during a long request. Source evidence was presented as an XML-escaped JSON array, and phase instructions requested identity fields absent from the advertised schema.

## Decision

At a new approved execution boundary, use the transactional coordinator's bounded incremental promotion to refresh the approved file endpoints. Freeze that generation through proposal retries and approval. Preserve the original diagnostic baseline through post-apply corrections. No automatic child is launched.

Make model ReplaceText offsets and lengths optional for unique nonempty expected text. The host derives original UTF-16 ranges; empty insertions and repeated text require an exact offset. Keep logical-newline matching and preserve literal source escapes; do not heuristically decode another JSON layer. The exact normalized proposal still passes scope, semantic, approval, and transaction checks.

Render known file-read evidence as decoded lines with all metadata, provenance, sensitivity, and original stored evidence preserved. Align phase/output/tool descriptions with the same identity-free envelope. Label the model wait `Generating edits`.

## Consequences

Manual rollbacks before a fresh execution no longer leave that execution targeting old approved-file bytes. Source snapshots outside the approved endpoints are reused. Changes after the boundary still conflict; refresh is never an approval bypass. The prompt removes unnecessary character-counting work and escaping, but provider latency remains variable and needs live measurement.
