# Require a semantic match for automatic memory recall

Status: Active.
Delivery track: Maintenance.
Prerequisites: Repository instructions, portable C# guardrails, ADR-59, and existing shared memory retrieval, reconciliation, and intelligence routing contracts.

## Objective and scope

Every automatic situational recall candidate, Ordinary or Intelligence, must have a valid compatible embedding with cosine strictly greater than its configured `SemanticMinimum`. Apply admission before candidate limits and reranking. Lexical and concept matches, caches, and fallback cannot override it. Semantic scoring unavailability returns no automatic situational memories with bounded diagnostics.

Reuse `HybridRepositoryMemoryRetriever`, its semantic match set, compatibility checks, cosine calculation, RRF ranks, and reranker. A host-owned `RequireSemanticMatch` request flag distinguishes automatic recall from independent explicit search and reconciliation and participates in ranking-cache identity.

## Preserved contracts

Standing preferences, explicit search, reconciliation, cancellation, and access controls remain unchanged. Do not change the threshold default/range, embedding model, normalization, tokenization, query truncation, or reranker thresholds. No migration or embedding rebuild is required by this change. Completed milestone contracts remain frozen.

## Implementation and acceptance

Implementation and deterministic regression coverage are applied. The optional real-model long-question test and hybrid ranking oracle also reflect mandatory admission; their native execution remains unverified.

1. Trace automatic recall, explicit intelligence search, and reconciliation through shared search.
2. Test strict below/equal/above admission, semantic-only positives, concept-only negatives, invalid/incompatible/missing vectors, and unavailable query scoring using a recording reranker.
3. Admit through the existing semantic set before constructing the rerank/fallback pools; retain lexical RRF evidence for admitted candidates.
4. Isolate successful and degraded ranking caches by policy and threshold; keep query-vector reuse.
5. Distinguish semantic threshold rejection from unavailable scoring and limit omissions without logging memory text or vectors.
6. Preserve standing preferences and independent-search/reconciliation behavior; validate each automatic ownership partition.

Owning acceptance: Scenario AM and Scenario BA in [acceptance scenarios](acceptance-scenarios.md). Manual procedures: MTP-261, MTP-262, MTP-277 and MTP-278 in [manual test plan](manual-test-plan.md).

## Validation

Local Windows validation:

- `dotnet restore src/Threadsmith.sln`: passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` (dedicated build tool): final C# state passed with no reported diagnostics. Configured build analyzers ran.
- `dotnet test --project tests/Threadsmith.ConversationContext.Tests/Threadsmith.ConversationContext.Tests.csproj --configuration Debug --no-restore`: 281 passed. The later full run also passed this project after partition-threshold assertions were strengthened.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,185 passed, 1 failed, 69 skipped. Failure: `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation`, whose delayed child did not publish its PID. No memory-retrieval failure was reported.
- `dotnet test --project tests/Threadsmith.Validation.Tests/Threadsmith.Validation.Tests.csproj --configuration Debug --no-build -- --filter-method '*BuildExecutor_CancelledBuild_ThrowsCancellation'`: same failure reproduced. Its cause is unresolved; the test was not modified.
- The full run preceded the final opt-in real-model test update. `dotnet test --project tests/Threadsmith.Embeddings.Local.Tests/Threadsmith.Embeddings.Local.Tests.csproj --configuration Debug --no-build` afterward: 5 passed, 7 skipped. Updated real-model cases were compiled but skipped. A requested repeat of the identical full-suite command was rejected by the host duplicate-call guard, not executed.
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Context/HybridRepositoryMemoryRetriever.cs src/Threadsmith.Core/RepositoryMemorySearch.cs tests/Threadsmith.ConversationContext.Tests/MemorySemanticAdmissionTests.cs tests/Threadsmith.ConversationContext.Tests/MemoryRerankingTests.cs tests/Threadsmith.ConversationContext.Tests/Plan34ConversationMemoryTests.cs tests/Threadsmith.Embeddings.Local.Tests/RealEmbeddingQualityTests.cs`: passed.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Context/HybridRepositoryMemoryRetriever.cs src/Threadsmith.Core/RepositoryMemorySearch.cs tests/Threadsmith.ConversationContext.Tests/MemorySemanticAdmissionTests.cs tests/Threadsmith.ConversationContext.Tests/MemoryRerankingTests.cs tests/Threadsmith.ConversationContext.Tests/Plan34ConversationMemoryTests.cs tests/Threadsmith.Embeddings.Local.Tests/RealEmbeddingQualityTests.cs`: reports pre-existing CA1869 and RCS1118 suggestions in unchanged portions of `RealEmbeddingQualityTests.cs`. The same check without that last file passed. No new scoped compiler/analyzer findings remained.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`: passed, 118 packaged files. Later changes touched only tests and this excluded source planning document.
- `git diff --check`: passed. Planning governance status-contamination searches returned no matches.

Status remains Active with validation limitations: the full-suite cancellation failure is unresolved; real-model integration, held-out relevance measurement, manual UI/provider procedures, macOS/Linux, and multi-platform CI were not run. No default was recalibrated. A bounded source review found no admission bypass in the inspected production paths; it did not establish end-to-end provider continuation behavior.

## Risks and follow-up

Useful weak-semantic memories can be excluded, and irrelevant strong-semantic memories can still pass. Automatic recall may return fewer or zero memories. Passing admission does not prove task applicability. Measure relevance after implementation before proposing a different default; no threshold adjustment belongs to this work.
