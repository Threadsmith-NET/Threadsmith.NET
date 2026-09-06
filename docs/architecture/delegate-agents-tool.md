# `delegate_agents` under the hood

This document explains the model-visible `delegate_agents` tool, which creates one bounded layer of read-only children. Each child can explore, propose an implementation, or review security, tests, performance, or architecture. Approved implementer preparation uses the existing mutation proposal application and delegation coordinator; ordinary conversation grants no write authority.

## Mental model

`delegate_agents` is a synchronous tool call backed by asynchronous child work:

```text
parent model
    |
    | delegate_agents({ agents: [...] })
    v
validate input and freeze authority
    |
    v
create one-level delegation plan
    |
    v
schedule bounded children by role concurrently
    |
    +--> assemble child context --> child model/tool loop --> final response
    +--> assemble child context --> child model/tool loop --> final response
    +--> assemble child context --> child model/tool loop --> final response
    |
    v
write joined checkpoint
    |
    v
retain responses separately from host-owned role/model/status metadata
    |
    v
project one compact model-visible result
```

The parent receives final responses, not child transcripts or hidden reasoning. Each child gets a fresh model request containing its frozen assignment, applicable repository instructions, governed evidence, and an explicitly narrowed tool inventory. Existing cooperative steering can add lower-authority user context at a model/tool boundary without changing that authority.

## End-to-end flow

### 1. Availability is decided before the model sees the tool

Application composition registers `delegate_agents` only when the configured model catalog can satisfy the child request's capability requirements. An ordinary final response has no required structured-output shape. Ordinary tool availability still applies, including repository trust, an open workspace, effective tool enablement, and invocation policy.

The parent receives an exact immutable snapshot of the tools visible for that model request. Child tool inheritance later resolves against that snapshot, not against a newer global registry state.

Primary implementation:

- `ConversationToolAvailability` decides whether delegation is compatible with the current model catalog.
- `ConversationToolSnapshotStore` freezes and resolves exact parent `ToolRegistration` identities.
- `ApplicationComposition` and `IntegrationComposition` compose the tool and its authorities.

### 2. The tool validates a deliberately small input

The model supplies only an array of child requests:

```json
{
  "agents": [
    {
      "task": "Trace the cancellation path.",
      "context": "Focus on delegation checkpoints.",
      "role": "explorer",
      "toolAccess": "readOnly"
    }
  ]
}
```

`agents[].role` is optional and defaults to `explorer`. The exact accepted values are `explorer`, `implementer`, `securityReviewer`, `testReviewer`, `performanceReviewer`, and `architectureReviewer`. Unknown roles, different capitalization, and unknown fields fail validation before scheduling. The model cannot choose child IDs, provider/profile/reasoning, budgets, deadlines, trust, sensitivity, workspace roots, individual tool IDs, approval, or concurrency. Host policy owns those values.

Primary implementation:

- `DelegateAgentsContracts` owns input/output DTOs, limits, and validation.
- `DelegateAgentsTool` owns the model-visible schema and execution entry point.

### 3. The host freezes a delegation plan

`DelegateAgentsPlanFactory` converts each request into an `AgentAssignment` with host-generated identities and immutable authority. The plan records the parent session/run, workspace, repository and baseline identities, attempt and generation, budgets, role, stopping rules, sensitivity, tool policy, and failure policy.

Conversation delegation creates:

- the requested role, with `Explorer` as the default;
- mode `ReadOnlyBaseline` for explorers and implementers, or `ReadOnlyReview` for reviewers;
- the common `agent-response/1` contract marker and runner version, with no required body format;
- no mutation, process, build, or test budget;
- delegation depth one;
- one frozen generation.

`readOnly` retains only eligible approval-free, non-network read tools from the parent snapshot. `inherit` may also retain eligible network-backed read tools. Both modes remove workflow, delegation, mutation, code/process execution, and approval-bearing tools. The child retains the caller's executable allowlist so retained read-only tools such as semantic and inventory tools can use their declared host-managed dependencies; this does not add tools or executable authority absent from the caller. Central policy rechecks every actual child invocation.

Primary implementation:

- `DelegateAgentsPlanning` creates the plan and narrows child authority.
- `AgentToolPolicy` scopes and validates every child invocation.
- `DelegationPlanValidator` validates identities, graph shape, budgets, and one-level authority.

### 4. The scheduler runs observed in-process children

`DelegationCoordinator` writes accepted and queued checkpoints, then calls `AgentRunScheduler`. The scheduler enforces:

- total queue capacity;
- global active-child capacity;
- per-parent active-child capacity;
- implementer capacity for the broader infrastructure;
- dependency ordering and hierarchical cancellation;
- bounded shutdown observation.

Every child is a normal asynchronous .NET task. There is no child agent process. Queue saturation returns a failed terminal checkpoint with terminal child outcomes, allowing `DelegateAgentsTool` to return a structured failure rather than leak a scheduler exception.

Progress writes carry monotonically increasing revisions. A stale progress write cannot replace a newer terminal checkpoint.

Primary implementation:

- `AgentRunScheduler` owns admission, concurrency, dependency observation, and cancellation.
- `DelegationCoordinator` owns lifecycle checkpoints and join disposition.
- `DelegationCheckpointStore` persists the latest revision.

### 5. Each child gets governed context, not parent history

`AgentRoleRunnerRegistry` selects the runner for each frozen assignment while retaining one parent join. The runners share model/tool execution through `ModelExplorerAssignmentRunner`; `ChildAgentPrompt` selects the Explorer, Implementer, SecurityReviewer, TestReviewer, PerformanceReviewer, and ArchitectureReviewer system-prompt amendments from deployed `Prompts/` assets. A role combines those instructions with host-selected model and eligible tools, not a required output template or role-output parser. It cannot grant tools or change host policy. `AgentContextAssembler` includes every eligible, non-stale parent evidence item allowed by the child sensitivity policy. Repository instructions are resolved independently for the child.

`AgentRoleModelConfiguration` validates trusted `agents:roleModels` at startup. `AgentRoleModelPolicy` holds the immutable role mappings and provider bindings. `AgentModelSelector` applies application assignment pin, role mapping, inherited preference, then compatible default precedence. Configured role routes and their fallback candidates use a catalog and dispatcher built without repository entries, preventing a same-ID repository provider override from redirecting the request or its credentials.

Only source `RoleConfiguration` sets `UsesTrustedCatalog`. `ApplicationPin`, `Inherited`, and `Default` retain ordinary effective-catalog dispatch and its normal repository configuration and secret-source rules. A model preference inherited from the parent does not gain trusted routing authority. Role and field names are case-sensitive, while provider IDs and reasoning names are case-insensitive. Invalid configuration fails startup; compatible runtime fallback does not excuse a malformed or unsupported startup mapping.

Selection records configured and effective provider/profile/reasoning, source, and any fallback reason in `AgentModelProvenance`. Every actual request is checked for capability, workload, capacity, sensitivity, cost, deadline, and provider compatibility before I/O. A changed profile requires rebuilding instructions and capacity estimates and checking again. Only host selection can make that fallback; model output cannot reroute the child. See [models for delegated roles](../operations/model-providers.md#models-for-delegated-roles) for configuration and startup errors.

`ChildAgentRequestFitter` validates the complete request against the selected model's actual context window. It never drops parent evidence, applicable `AGENTS.md` sources, or configured prompt append sources. When the complete request cannot fit, execution fails explicitly instead of silently reducing caller-provided context. The request records every delivered evidence ID.

The request does not contain:

- parent or sibling transcripts;
- hidden reasoning;
- repository/network tools absent from the exact parent request; the child-local `read_agent_evidence` lookup is a separate read capability restricted to previously delivered IDs;
- mutable global model or tool state;
- initial evidence removed by token pressure.

Primary implementation:

- `AgentContextAssembler` selects every policy-eligible governed evidence item.
- `ChildAgentRequestFitter` validates the complete provider request and records delivered evidence IDs.
- `ChildAgentPrompt` renders instructions, role amendment, assignment, evidence, and tools.
- `ChildAgentInstructions` resolves the applicable repository instruction bundle.

### 6. The child returns its own final response

`ChildAgentHistory` tracks completed call/result batches plus their progress or correction messages. The common active-turn compactor can replace an older, previously delivered prefix with working notes, using child-specific token/percentage triggers and a recent-context target. Initial role/task/instruction/evidence messages and user steering are never eligible. The newest batch remains exact. Replacement must reduce the fully estimated request by the configured minimum, including the summary and archived evidence index. A failed attempt leaves the same messages active and observes the configured interval before retry. `HistoryRewriteGeneration` invalidates provider continuation state after each replacement. Normal request prefixes remain stable between replacements.

Child summaries use the already sanitized result content for useful paths and code locations. They do not send a duplicate raw-provenance file inventory or append an empty inventory claiming that no files were read. Every archived evidence ID remains in the child's separate index, without limiting it to the summary generator's metadata projection.

The summary request uses the child's selected provider/profile and the shared deployed compaction prompts, with no tools or requirements on the child's eventual response. Summary usage contributes to both session and child totals. An ActivitySource named `Threadsmith.Execution.ChildAgentCompaction` records before/after estimates and outcome without source content. Tuning lives in trusted `agents:delegation:compaction`; see the [user guide](../user-guide.md#subagent-history-and-evidence).

`ChildAgentEvidenceTool` is a request-local capability, not a new parent or repository tool registration. The loop passes it through the central invocation pipeline with a context allowing only that lookup. All ordinary inspection registrations still come from the exact parent snapshot. Typed lookup arguments and the ordinary batch are prepared before execution; repository inspections use the existing batch pipeline and memory lookups use direct pipeline invocation. The lookup checks session, child run, delivered-ID membership, and current evidence staleness. It returns only the original sanitized stored content, with no fresh filesystem or network access. A lookup does not duplicate evidence or gain another child's access. Children with deny-all tool policy or an explicit lookup deny do not receive this capability.

`ChildAgentModelLoop` streams model output, executes validated read-only tool batches, stores each tool result as child-owned evidence, and sends a bounded tool-result continuation back to the child. Valid JSON tool content is embedded directly in the continuation envelope instead of being serialized as an escaped JSON string inside that envelope; non-JSON content remains a string. Provider adapters still encode the complete envelope according to their transport protocol. One immutable tool-schema estimate is reused across continuation rounds. Wire accounting includes message content, tool-call IDs, tool names, framing, tool schemas, and output reserve.

Instructions encourage focused inspection, batching independent calls, and using relevant semantic or structural evidence. These are guidance for useful work, not a required response template or a semantic acceptance test.

`ChildAgentEvidenceProgressTracker` seeds coverage from all delivered parent evidence and observes the exact sanitized result plus attributable sources for each child tool invocation. After every batch, the host appends an advisory progress message that distinguishes newly attributed files/source identities from merely distinct result payloads. A different payload without attributable source expansion is not represented as evidence-coverage progress. Feedback discourages equivalent repeated requests while telling the Explorer to continue with another relevant approach when a requested claim remains unsupported; one empty, noisy, irrelevant, or incomplete result is not a terminal gap. This signal is not a quota, role-independent workflow, or authority boundary: it never blocks a tool, prescribes a retrieval sequence, removes a result, drops prior messages, or replaces parent evidence, applicable `AGENTS.md`, configured prompt appends, or inherited policy.

The final body is stored unchanged in `AgentRunOutcome.Response`. Plain text, JSON, whitespace, and an empty string are all permitted. A non-null response distinguishes a new ordinary reply from legacy structured outcomes; no summary, findings, review fields, citation GUIDs, or other body contents are required. The loop does not parse ordinary responses into role DTOs or issue response-format repair turns.

The ledger records model tokens, tool calls, evidence items, files, bytes, and actions. Each request must still fit the selected model's real context window and provider output limit. Active request, tool-payload, transport, deadline, and final-envelope resource controls remain separate from the unrestricted response body. Tool arguments still undergo central validation and policy checks. Cancellation or a transport failure remains a terminal failure/cancellation, not a successful empty reply.

Completion is a host transport/join status, not a judgment that the answer is correct, useful, or complete. A clean review grants no approval and proves no test execution. Implementer suggestions remain read-only even when they name new files.

Primary implementation:

- `ChildAgentModelLoop` owns streaming, continuations, tool batches, and resource charging.
- `ChildAgentEvidenceProgressTracker` measures distinct coverage without trimming context or enforcing a quota.
- `DelegationOutcomeClassifier` distinguishes ordinary response completion from legacy structured results and approved mutation protocols.
- `AgentBudgetLedger` enforces hierarchical resource reservations.
- `ModelWireEstimator` performs conservative provider-neutral request accounting.

### 7. Responses cross a durable join boundary

The host checks frozen assignment identity, role, generation, mode, and terminal status independently of the response body. A successful response is exposed only after the authoritative joined checkpoint is durable. Cancellation or stale-generation output cannot acquire authority by returning convincing prose.

Ordinary responses are opaque child claims, not verified findings or parent evidence. The host does not infer structured findings from JSON-looking text, invent citations, or promote claims that tests passed. Governed tool evidence and delivered-ID accounting remain available independently of the final body.

Core retains legacy structured outcome/checkpoint DTOs, which `DelegationCheckpointStore` deserializes for persisted compatibility. Their finding-admission path still validates identity, generation, schema, delivered citations, and provenance before atomic parent-evidence publication. This does not retain role-output parsers or impose a schema on new ordinary replies.

Primary implementation:

- `AgentFindingAdmission` validates identity, generation, schema, citation set, and provenance.
- `EvidenceStore.TryAddBatchAsync` owns the atomic evidence/commit gate.
- `DomainEventStream.PublishCommittedBatchAsync` prevents observers from seeing uncommitted evidence and bounds committed subscriber delivery.

### 8. The parent receives a compact projection

`DelegateAgentsResultProjector` maps the durable checkpoint to a model-facing envelope containing the returned child text and separate host metadata. `DelegateAgentsResultRenderer` applies the active final-envelope bounds.

Ordinary text is not semantically deduplicated, graded, or converted into reviewer/implementer fields. Legacy structured result projection remains compatible without treating new JSON-looking replies as legacy DTOs.

The result contains:

- delegation and assignment IDs and roles;
- aggregate and child statuses;
- configured and effective model provenance, selection source, and fallback reason;
- final child responses;
- host-reported failures and any envelope omissions;
- bounded token and tool-call usage.

The result excludes child conversation transcripts, hidden reasoning, provider transport payloads, and mutable host objects. A response may contain unsupported claims; returning it does not verify those claims.

## Failure and cancellation semantics

The important rule is that every path becomes inspectable terminal state:

| Condition | Result |
|---|---|
| Every ordinary child completes and joins with a present response, even empty | `Completed` |
| At least one child completes and another fails or is cancelled | `Partial` |
| No child completes successfully | `Failed` |
| Parent cancellation wins before join commit | `Cancelled` |
| Queue capacity rejects admission | `Failed` with terminal child outcomes |
| Legacy structured finding validation or evidence join fails | `Failed`; no partial parent evidence |
| Cancellation arrives after join commit | Joined result remains authoritative |

Cancellation is cooperative, but non-cooperative work is still observed at host-owned wait boundaries. A cancelled or obsolete generation cannot publish an authoritative joined response or legacy finding later. Ordinary formatting, missing role fields, or answer quality do not determine host completion.

## Approved implementation and persisted inspection

Application composition selects `ApprovedImplementerProposalApplication` for the execution orchestrator when configured model profiles are available; the empty-catalog offline flow keeps `MutationProposalApplication` directly. The adapter connects that proposal application to the existing delegation coordinator for approved implementation and correction turns. Its child uses `approved-implementer-preparation/1` and prepares a candidate scoped to the accepted plan through the existing proposal validation and correction rules. This actual mutation protocol is distinct from an ordinary read-only implementer's unrestricted `agent-response/1` reply. Preparation does not stage or apply changes. The parent stages the prepared proposal only after the authoritative join and a current-baseline check, then uses the existing exact-diff approval, transaction, validation, and correction lifecycle. A failed or cancelled join cannot stage it.

Approved child request assembly has an explicit capacity contract. `MutationProposalApplication` sets `ContextAssemblyRequest.DeferAgentModelCapacityValidation` only for a preparation child. `ContextAssembler` accepts the flag only in `ImplementationModelTurn` or `CorrectionModelTurn` with an approved plan and mutation baseline. It must preserve the complete provisionally admitted context until final agent selection, deferring selected-profile pressure reduction and all final selected-profile fit checks, including complete wire input. Provisional admission bounds, source policy, and sanitization remain in force; the flag does not admit unlimited evidence or grant provider access.

The preparation host must pass that provisional request through `AgentModelSelector.SelectForRequest` before provider I/O. The check includes messages, canonical tools, provider instructions, sensitivity, and each candidate's own output reserve. A fallback requires reassembling context, instructions, output limits, and wire estimates, then checking again before dispatch. A request that cannot fit or whose selection does not stabilize fails before provider I/O. The flag defaults to `false`, is not configurable by repository data or the model, and leaves ordinary context reduction and capacity validation unchanged. Provisional inspection values alone never prove that a request fits the eventual provider.

This preparation path does not automatically apply parallel worktree changes. Existing worktree acquisition, frozen worker change-set, and parent integration APIs retain their separate authorization and conflict checks.

`Checkpoint.Assignments` stores role, contract marker, runner version, and configured/effective provider/profile/reasoning with source and fallback. Outcomes retain their effective model provenance and optional `Response`. An empty ordinary response is distinct from a missing/null response in a legacy structured checkpoint. `/agents <id>` projects the role and effective provider/profile/reasoning/source/fallback alongside lifecycle state. Reading persisted state does not resume work: the coordinator has no automatic resume API for interrupted delegated model loops. Further delegation requires a new generation; it cannot continue an old provider stream. Approved-plan execution retains its existing resume lifecycle.

## Component review map

For a focused code review, read these files in order:

1. `DelegateAgentsTool.cs`: public tool contract and four-step execution path.
2. `DelegateAgentsContracts.cs`: DTOs, limits, and strict input validation.
3. `DelegateAgentsPlanning.cs`: host-owned assignment and authority construction.
4. `DelegationOrchestration.cs`: scheduler, checkpoints, cancellation, and join arbitration.
5. `AgentRoleRunnerRegistry.cs`, `ModelExplorerAssignmentRunner.cs`, `ChildAgentPrompt.cs`, and `Prompts/`: runner selection, role amendments, child composition, and parent join.
6. `ChildAgentRequestFitter.cs`: exact initial request fitting.
7. `ChildAgentModelLoop.cs`: bounded model/tool continuation loop.
8. `AgentContext.cs`: child context selection and finding admission.
9. `EvidenceStore.cs`: atomic evidence batch commit.
10. `DelegateAgentsResultProjector.cs` and `DelegateAgentsResultRenderer.cs`: final bounded output.
11. `AgentRoleModelConfiguration.cs`, `AgentRoleModelPolicy.cs`, and `AgentModelSelection.cs`: trusted routing, precedence, and request checks.
12. `ApprovedImplementerProposalApplication.cs`, `ApprovedImplementerAssignmentRunner.cs`, and `MutationProposalApplication.cs`: approved proposal preparation and parent staging.
13. `ContextContracts.cs` and `ContextAssembler.cs`: approved-child capacity deferral, provisional context, and ordinary assembly checks.

Application composition connects these services to provider dispatch, persistence, terminal inspection, and the central tool policy.

## Deliberate non-goals

The conversation tool does not:

- create recursive agents;
- allow children to mutate files or run processes;
- merge, commit, push, rebase, or cherry-pick;
- transfer raw child conversation state to the parent;
- let the model select authority or resource limits;
- infer runtime-only evidence that no authorized tool returned;
- treat a worktree as a security sandbox.

Operational usage and troubleshooting are documented in [Parallel-agent operations](../operations/parallel-agents.md).
