# Direct-editing removal ledger

This records the current cutover inventory for [the implementation contract](maintenance-direct-editing-incremental-semantics.md). It is not a claim that all implementation acceptance or performance work is complete.

## Current ownership

Ordinary model and tool continuations remain in `SessionApplication`. `edit_source` runs through the existing tool invocation pipeline. `MutationMaterializer` resolves ordered exact anchors and lifecycle operations; `SourceEditApplication` owns exact authorization, receipts, cumulative scope and final outcomes. `MutationEffectJournal` records durable intent and invokes the existing transactional workspace writer. The existing Roslyn engine, compilation coordinator and semantic refresh owner provide versioned advisory feedback. `ValidationApplication` and `ValidationPipeline` remain available for explicit validation. The model decides when to run builds and tests after resolving incremental compiler feedback; ordinary completion records disk effects without triggering validation.

## Removed live artifacts

- Mandatory plan generation, plan sanity and approval, host-selected steps/tranches, replan and completion tools.
- `ExecutionOrchestrator`, its router, `MutationProposalApplication`, prepared proposals and their separate model request loop.
- Plan policy command, service, repository store, binding, trust writer and path-safety owner.
- Planning admission branches, plan limits, incremental-plan and mutation-batching settings and composition registrations.
- Phase-specific context/output assets and plan/proposal/replan/pre-mutation correction assets, with their catalog entries.
- The retired pre-mutation analyzer interface, DTO family, registry adapter and exclusive helpers; compiler findings now use the direct-edit advisory analyzer.
- Unused mutation mismatch suggestions/fingerprints, plan checkpoint writer, structured plan/mutation model-output surfaces and validation-correction assets without callers.
- Unregistered plan-step isolated worker coordination, partitioning and integration owners and their exclusive tests; ordinary delegation and generic workspace isolation remain.
- Plan workflow host actions in supported skill manifests; maintained analyzer/package skills return advisory findings instead.
- Tests solely exercising the retired protocols. Ordinary conversation, replay/memory, persistence, workspace safety and current semantic coverage remain in their applicable suites.

Current operations, user guidance, resource examples, prompt references, acceptance scenarios and manual procedures describe direct editing. Retired manual cases keep stable IDs and identify replacement procedures. ADRs retain their original rationale with explicit direct-editing amendments.

## Configuration compatibility

Known retired keys are limited to `planning:approvalPolicy`, `planning:approvalRepositoryIdentity`, `planning:incrementalPlans:{enabled,targetSteps,targetFiles}`, `execution:mutationBatching:{targetMutations,targetFiles,targetMutationCharacters}`, `execution:maxPlanningToolRounds`, `execution:maxPlanSanityIssues`, and the six former `limits:plan` fields. Writable repository JSON migrates those exact keys while preserving unrelated configuration; read-only/external layers ignore them with diagnostics. Unknown configuration remains subject to ordinary validation.

Supported mutation approval policies remain. Legacy `TrustPlan` reads conservatively as `ReviewAll` and cannot be newly selected. Negative absence and migration tests intentionally name retired tokens; they do not exercise an executable old flow.

## Retained historical compatibility

`ImplementationPlan` and associated payload types remain only where old durable plan events require them. Reserved event discriminators and enum values, historical projection readers, checkpoint schemas 1–2 and checkpoint readers remain inspectable. No remaining plan steps are resumed. Unresolved legacy-write detection still fences new mutations because an interrupted old write must not be silently replayed or discarded.

Current mutation outcomes, their persistence/artifacts and diagnostic DTOs remain live direct-edit capabilities. Completed implementation documents stay frozen under planning governance. Historical ADR text and manual retirement notices are not current execution instructions.

## Verification limits

Source and prompt inventories are checked against actual callers and deployed catalog entries. The October 5 final solution build passed with zero warnings and errors; the latest full regression run contained 3,657 tests: 3,625 passed, 31 skipped, and one outdated MTP discovery fixture failed. After updating that fixture to the JSON discovery contract, all 36 tests in the affected validation project passed on rerun. No test failures remain outstanding. All 284 source prompt assets match both inventories. Clean-context source/artifact reviews and the implementation-contract alignment review found no remaining actionable findings in the assessed paths. Prior pre-cutover test counts and extraction-only results do not establish current correctness.

Live GLM 5.2 runs verified advisory precheck/committed feedback, repeated source reads, break/repair edits, a model-selected native build, and two individually selected passing test cases. A separate run completed with an unresolved advisory compiler error without starting build/test validation. Both runs recorded no automatic final validation. The local evidence is `.inbox/live-direct-edit-20261005/verified-model-directed-validation.json`. Enabled tools are no longer filtered by phase, side effects, or a conversation-availability flag; trust and explicit allow/deny policy remain enforced.

The implementation contract's representative performance experiment and remaining full runtime acceptance evidence remain separate outstanding work. This ledger must not be used to claim that work complete.
