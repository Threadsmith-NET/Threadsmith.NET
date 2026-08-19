# Semantic Confidence

Threadsmith.NET reports how much compiler-backed knowledge supports every semantic result. Confidence is host-owned state; it is never inferred by a model.

| Level | Available knowledge | Semantic tool behavior |
|---|---|---|
| `None` | No usable project state | Semantic tools are unavailable. |
| `TextOnly` | Project-file metadata and repository text | Reference search requires an explicit text-fallback opt-in and labels every match `TextOnly`. |
| `ProjectGraphOnly` | Evaluated solution/project graph without usable compilations | Project and target-framework inventory remains available; compiler symbol operations are unavailable. |
| `PartialCompilation` | One or more projects compile | Symbol, reference, and implementation results are available for compiled projects and carry `PartialCompilation`. |
| `FullSemantic` | Every loaded project has a usable compilation | All semantic read tools are available and results carry `FullSemantic`. |

`TrustedRead` loads text/project metadata without executing MSBuild. `TrustedBuild` permits `MSBuildWorkspace` evaluation and compilation. Direct project selections use `OpenProjectAsync`; solution selections use `OpenSolutionAsync`. `FullSemantic` requires every expected project to be present and compiled without a workspace failure. Missing or omitted projects are retained as degraded project facts and prevent full confidence. Search results contain a stable symbol identity, project, target framework, file, one-based source range, generated-code flag, linked-file flag, and confidence.

File changes are queued during a turn. At the next turn boundary, affected semantic state is invalidated and confidence is demoted to `ProjectGraphOnly`; promotion reloads the last solution. Non-cooperative Roslyn/MSBuild work uses cancellation with abandon-and-discard and a bounded cleanup backstop.

The composition root maintains one engine per `WorkspaceId`. Repository lifecycle callbacks enqueue loads onto a separate worker so confidence publication cannot re-enter and deadlock the event subscription. Semantic tools resolve only the workspace in host-owned invocation context and declare its repository root to policy. `SemanticConfidenceChanged` updates projections when confidence changes; `SemanticLoadCompleted` records every terminal load result, including `None`, so the TUI can distinguish `Loading...` from `Unavailable`.
