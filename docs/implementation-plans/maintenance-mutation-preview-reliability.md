# Mutation preview reliability

Status: Complete

Delivery track: Maintenance

Prerequisites: serial parent-run mutation generation and transactional baseline promotion.

## Scope

Apply [ADR-58](../architecture/adr-58-current-mutation-baselines-and-text-anchors.md). Refresh approved endpoints at execution start, remove mandatory offset arithmetic for unique source anchors, preserve existing logical-newline recovery, simplify source evidence, align the advertised proposal contract, and identify the model wait in the UI.

## Verification

Exercise Scenario L and MTP-169. Automated regressions cover manual rollback between independent sessions, exact approval and later conflicts, LF/CRLF proposals without numeric offsets, literal source escapes, malformed encodings, ambiguous anchors and empty insertions, and source-evidence provenance/truncation. Run the solution build and affected mutation, orchestration, context, runtime, architecture, and delegation suites. The solution build and affected regression suites passed. Live provider latency is a separate measurement.
