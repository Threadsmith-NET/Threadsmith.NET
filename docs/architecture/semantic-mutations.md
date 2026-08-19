# Semantic Mutations

M5 supports two Roslyn-backed operations: symbol rename and bounded syntax-node replacement. Roslyn objects remain internal to `Threadsmith.DotNet`; each operation emits a host-owned `MutationSet` for the transactional workspace.

## Confidence precondition

Both operations require `SemanticConfidenceLevel.PartialCompilation` or `FullSemantic`. `TextOnly` and `ProjectGraphOnly` requests fail with an actionable instruction to restore compiler confidence or propose an explicitly approved text patch. Confidence is captured at the mutation turn boundary.

## Rename

- Resolve a stable documentation-comment symbol id in the compiled project subset.
- Use Roslyn `Renamer` with overload/string/comment/file rename disabled for the M5 bounded scope.
- Emit one full-document text replacement per distinct changed baseline file.
- Set `RelatedSymbolId` on every emitted mutation.
- Omit non-baseline and uncompiled-project documents with warnings; never silently claim a full-solution rename at partial confidence.

## Bounded syntax replacement

- Require an exact Roslyn node span.
- Support expressions, statements, and member declarations.
- Parse and reject replacement syntax containing diagnostics.
- Preserve trivia and format the annotated replacement region.
- Emit one baseline-hashed full-document text replacement and populate `RelatedSymbolId` when supplied or resolvable.

Preview, approval, conflict detection, commit, and rollback are unchanged from `docs/architecture/mutation-model.md`.
