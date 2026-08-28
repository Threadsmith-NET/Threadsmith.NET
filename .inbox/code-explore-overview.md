# How `code_explore` works

`code_explore` is Threadsmith.NET's source-bearing, compiler-aware C# exploration tool. It is intended for questions such as “where is repository trust checked?”, “show me this exact method”, or “how does this entry point reach that handler?” It combines deterministic query interpretation, a loaded Roslyn solution, bounded semantic navigation, and current source reads. It does **not** send the query to a second model, execute query text, or claim to prove every possible runtime path.

> The examples below are simplified for readability. Property names and result shapes are representative, but large metadata collections and source text are abbreviated.

## At a glance

```text
Natural-language user question
        |
        v
Model chooses code_explore and supplies a structured request
        |
        v
Host validates trust, workspace, paths, limits, and model budget
        |
        v
Query is tokenized into identifiers, qualified names, paths, and terms
        |
        v
Roslyn declaration catalog is searched and deterministically ranked
        |
        v
Top candidates become exact symbol/path anchors
        |
        v
Roslyn resolves symbols and optionally traverses calls/references/dispatch
        |
        v
Host rereads current files, checks identity and policy, and emits bounded source
        |
        v
Structured result: ranked candidates + source + coverage + follow-ups
```

## 1. From the user's words to a tool request

The model sees `code_explore` only when Threadsmith has an opened semantic workspace and repository policy permits the tool. The user can ask an ordinary question; they do not need to know the JSON schema. The model decides that compiler-aware, source-bearing exploration is appropriate and translates the question into a `CodeExploreRequest`.

A minimal request needs a `query`:

```json
{
  "query": "How does code exploration rank natural language candidates?",
  "mode": "survey"
}
```

The model may also provide stronger anchors when it already has them:

- an exact symbol name, such as `Threadsmith.Tools.CodeExploreTool.ExecuteAsync`;
- a stable semantic symbol ID returned by an earlier semantic tool;
- a repository-relative `.cs` path, optionally with a line or exact range;
- explicit limits on files, characters, traversal depth, and elapsed time;
- a mode: `auto`, `survey`, `flow`, or `impact`.

This distinction matters: the tool receives the structured request selected by the model, not an unrestricted command to “understand” the entire user message. Query text is inert data and is never provider-reranked or executed.

## 2. Host-side validation and fencing

Before Roslyn navigation starts, the tool adapter checks that:

- the repository has the required `TrustedBuild` trust level;
- a workspace is open;
- the query, anchor counts, paths, line ranges, timeouts, and output limits fit host ceilings;
- requested paths are within repository invocation policy;
- the result can fit the selected model's request budget.

The semantic service captures one workspace snapshot and its monotonically increasing generation. Every resolution, source section, and continuation belongs to that snapshot. If the workspace changes while a late query is completing, the stale result is rejected rather than being mixed with the newer solution.

The snapshot may have full or partial semantic confidence. Partial compilation can still produce useful evidence, but the result says that unloaded or uncompilable projects may be missing.

## 3. Interpreting natural language deterministically

When there are no explicit anchors, the service tokenizes `query` and classifies bounded pieces as:

- exact-looking C# identifiers;
- qualified names;
- stable symbol IDs;
- repository-relative C# path-like spans;
- ordinary search terms;
- ignored stop words.

It also creates simple normalized variants, such as identifier segments and a singular form for some plural terms. For example, “How are natural-language candidates ranked?” contributes useful terms such as `natural`, `language`, `candidate`, and `rank`, while conversational words such as “how” are ignored.

There is no embedding search and no LLM relevance pass inside the tool. The same snapshot, request, policy, and limits produce the same ordering.

## 4. Building and ranking the Roslyn declaration catalog

The service builds or reuses a catalog keyed by workspace ID and generation. It walks compiled Roslyn projects, ordinary documents, and already-loaded source-generated documents. For each supported declaration syntax node—types, methods, constructors, properties, fields, events, operators, delegates, enum members, and similar declarations—it asks the document's `SemanticModel` for the declared `ISymbol`.

Each catalog entry contains host-owned data derived from Roslyn, including:

- a stable semantic identity;
- simple, metadata, display, and fully qualified names;
- containing type and namespace;
- declaration kind, project, target framework, file, and source range;
- generated, linked, and test classifications;
- normalized terms derived from symbol and location metadata.

Entries disallowed by repository path policy are removed before ranking. Remaining entries receive explainable reasons and scores. Strong signals include an exact qualified-name match, a distinctive identifier, multiple uncommon matching terms, containing-type corroboration, or a matching C# path. Test and generated declarations are normally de-emphasized unless the query asks for them; same-file candidates can corroborate one another.

Roslyn then supplies a small, bounded connectivity boost. For the strongest seeds, the tool looks at outgoing calls, callers, implementations, and overrides. A candidate already present in the ranked set can move upward when that compiler-known graph connects it to a strong seed. Stable tie-breakers—kind, path, line, and semantic ID—make ordering reproducible.

The highest-ranked candidates, up to the anchor limit, become stable symbol-ID anchors for the next stage. The output retains candidate summaries and selection reasons so the caller can see why a declaration was or was not selected.

## 5. Resolving anchors and navigating code with Roslyn

All routes eventually converge on exact anchors:

- **Symbol name:** Roslyn resolves a simple, qualified, metadata, or documentation-style identity and reports ambiguity alternatives when necessary.
- **Stable symbol ID:** the ID is resolved back into the captured solution.
- **Path:** the service locates an allowed C# document and selects the whole file, containing declaration, single line, tail, or exact range according to the anchor.

After resolution, the requested mode controls the surrounding semantic evidence:

- **`survey` / `auto`:** emphasize the selected declarations and a compact structural neighborhood.
- **`flow`:** between at least two resolved source-bearing symbols, search bounded call paths and connector symbols. Edges are labelled as compiler-proven calls or known runtime-dispatch boundaries.
- **`impact`:** collect a compact, bounded blast radius such as callers, implementations, and related declarations.

Virtual/interface dispatch, delegates, reflection, runtime registration, external assemblies, and incomplete compilation prevent whole-program certainty. Rather than inventing a path, the result records dispatch branches, boundaries, omissions, confidence, and traversal limits.

## 6. Returning current source safely

Roslyn identifies semantic spans, but the adapter—not Roslyn alone—owns file access. A policy-aware source reader rechecks every path, reads a bounded regular file, computes a SHA-256 digest, and makes sure the semantic span still corresponds to current text. Returned source is line-numbered and marked `complete`, `partial`, `omitted`, or `drifted`.

The service allocates a total character budget across ranked sections, with per-file and file-count ceilings. If it cannot return everything, it emits digest- and generation-bearing continuation targets for focused follow-up calls. Optional associated-artifact discovery can also return bounded, inert text such as project metadata, configuration, prompts, or additional documents when the selected C# evidence establishes a relationship.

If an unchanged, complete source range is already present verbatim in the current canonical model request, the host can replace the duplicate with a compact back-reference. Provider memory, a summary, an older generation, or a model assertion is not sufficient proof that source is visible.

Finally, the adapter confines every location and artifact to repository policy again and trims lower-priority metadata/source to the selected model budget. The result is a host-owned DTO; Roslyn types never cross the tool boundary.

## Example A: broad architecture question

### Natural-language input

```text
How does code exploration rank natural-language candidates?
```

### Representative tool request

```json
{
  "query": "How does code exploration rank natural-language candidates?",
  "mode": "survey",
  "limits": {
    "maximumAnchors": 6,
    "maximumFiles": 4,
    "maximumSourceCharacters": 12000
  }
}
```

### Abridged output

```json
{
  "workspaceGeneration": 42,
  "confidence": "fullSemantic",
  "queryInterpretation": {
    "exactIdentifiers": [],
    "qualifiedNames": [],
    "terms": ["candidate", "exploration", "language", "natural", "rank"],
    "ignoredTerms": ["does", "how"]
  },
  "discovery": {
    "catalogEntryCount": 3180,
    "candidateCount": 9,
    "selectedCount": 6,
    "catalogComplete": true
  },
  "candidateSummaries": [
    {
      "symbol": "...RankNaturalLanguageCandidates...",
      "filePath": "src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs",
      "tier": "distinctiveIdentifier",
      "reasons": ["exactIdentifier", "multiTerm"],
      "selected": true
    }
  ],
  "fileSections": [
    {
      "filePath": "src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs",
      "declaration": "RankNaturalLanguageCandidates",
      "source": {
        "completeness": "complete",
        "numberedLines": ["5209: private static ...", "..."],
        "fileSha256": "..."
      }
    }
  ],
  "coverage": {
    "symbolResolutionComplete": true,
    "semanticCoverageComplete": true,
    "sourceComplete": true
  },
  "omissions": []
}
```

The model can answer from the returned implementation rather than guessing from filenames or the tool description.

## Example B: exact method and bounded impact

### Natural-language input

```text
Show CodeExploreTool.ExecuteAsync and the main code it invokes. What would be affected by changing that handoff?
```

### Representative tool request

```json
{
  "query": "CodeExploreTool.ExecuteAsync handoff and impact",
  "mode": "impact",
  "exactSymbolAnchors": [
    "Threadsmith.Tools.CodeExploreTool.ExecuteAsync"
  ],
  "limits": {
    "maximumBlastRadiusItems": 12,
    "maximumFiles": 5
  }
}
```

### Abridged output

```json
{
  "resolvedAnchors": [
    {
      "input": "Threadsmith.Tools.CodeExploreTool.ExecuteAsync",
      "kind": "symbolName",
      "outcome": "resolved",
      "selectedLocation": {
        "filePath": "src/Threadsmith.Tools/CodeExploreTool.cs",
        "range": { "startLine": 39, "endLine": 68 }
      }
    }
  ],
  "fileSections": [
    {
      "filePath": "src/Threadsmith.Tools/CodeExploreTool.cs",
      "source": { "completeness": "complete", "numberedLines": ["..."] }
    }
  ],
  "blastRadius": {
    "items": [
      {
        "relationship": "invokes",
        "symbol": "ICodeExploreService.QueryCodeExploreAsync"
      }
    ],
    "complete": false,
    "omissions": ["Impact is a bounded static slice, not whole-program proof."]
  },
  "continuationTargets": [
    {
      "kind": "symbolId",
      "anchor": "...QueryCodeExploreAsync...",
      "workspaceGeneration": 42,
      "reason": "Inspect the resolved service implementation in a focused follow-up."
    }
  ]
}
```

## Example C: follow a compiler-known flow

### Natural-language input

```text
How does CodeExploreTool.ExecuteAsync reach the natural-language discovery routine?
```

### Representative tool request

```json
{
  "query": "flow from ExecuteAsync to DiscoverNaturalLanguageCodeExploreAsync",
  "mode": "flow",
  "exactSymbolAnchors": [
    "Threadsmith.Tools.CodeExploreTool.ExecuteAsync",
    "Threadsmith.DotNet.AdvancedSemanticQueryService.DiscoverNaturalLanguageCodeExploreAsync"
  ],
  "limits": {
    "maximumFlowDepth": 4,
    "maximumFlowPaths": 4
  }
}
```

### Abridged output

```json
{
  "flow": {
    "paths": [
      {
        "nodeIds": ["ExecuteAsync", "QueryCodeExploreAsync", "DiscoverNaturalLanguageCodeExploreAsync"],
        "complete": true
      }
    ],
    "edges": [
      {
        "caller": "ExecuteAsync",
        "callee": "QueryCodeExploreAsync",
        "proofKind": "compilerKnownDispatchBoundary"
      },
      {
        "caller": "QueryCodeExploreAsync",
        "callee": "DiscoverNaturalLanguageCodeExploreAsync",
        "proofKind": "compilerProvenCall"
      }
    ],
    "boundaries": [
      {
        "kind": "runtimeDispatch",
        "reason": "The adapter calls ICodeExploreService; runtime composition chooses its implementation."
      }
    ]
  }
}
```

The interface call is deliberately reported as a dispatch boundary. Roslyn can enumerate compiler-known implementations, but the tool does not pretend static analysis proves which implementation every runtime composition will select.

## How to read a result

For a reliable answer, use the result in this order:

1. Check `workspaceGeneration` and `confidence`.
2. Review `queryInterpretation`, ranked candidates, and resolution outcomes to confirm the intended symbols were selected.
3. Treat `fileSections` as the primary implementation evidence.
4. Treat flow and impact as bounded static evidence, not runtime proof.
5. Read `coverage` and `omissions`; a useful result can still be incomplete.
6. Use `continuationTargets` for a smaller, exact follow-up instead of repeating the broad query.

In short, `code_explore` turns conversational intent into deterministic candidate selection, uses Roslyn to resolve and navigate compiler-known code structure, then returns only policy-confined, budgeted, current source and explicit evidence about what it could not establish.
