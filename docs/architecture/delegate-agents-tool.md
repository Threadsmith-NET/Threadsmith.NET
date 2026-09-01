# `delegate_agents` under the hood

This document explains how ordinary conversation delegation works without requiring a line-by-line review of the implementation. It covers the model-visible `delegate_agents` tool, which creates bounded read-only Explorer children. Threadsmith's broader parallel-agent infrastructure also supports approved implementation and review workflows, but the conversation tool cannot activate those roles.

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
schedule bounded Explorer children concurrently
    |
    +--> assemble child context --> child model/tool loop --> cited findings
    +--> assemble child context --> child model/tool loop --> cited findings
    +--> assemble child context --> child model/tool loop --> cited findings
    |
    v
write joined checkpoint
    |
    v
atomically admit validated findings into parent evidence
    |
    v
project one compact model-visible result
```

The parent receives no child transcript and cannot interact with a child mid-run. Each child gets a fresh model request containing only its frozen assignment, applicable repository instructions, bounded governed evidence, and an explicitly narrowed tool inventory.

## End-to-end flow

### 1. Availability is decided before the model sees the tool

Application composition registers `delegate_agents` only when the configured model catalog contains a profile that can satisfy the child contract: streaming, tool calls, and structured output. Ordinary tool availability still applies, including repository trust, an open workspace, effective tool enablement, and invocation policy.

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
      "toolAccess": "readOnly"
    }
  ]
}
```

The strict schema rejects unknown fields. The model cannot choose roles, child IDs, model profiles, reasoning, budgets, deadlines, trust, sensitivity, workspace roots, individual tool IDs, approval, or concurrency. Trusted host options own those values and keep them inside compiled ceilings.

Primary implementation:

- `DelegateAgentsContracts` owns input/output DTOs, limits, and validation.
- `DelegateAgentsTool` owns the model-visible schema and execution entry point.

### 3. The host freezes a delegation plan

`DelegateAgentsPlanFactory` converts each request into an `AgentAssignment` with host-generated identities and immutable authority. The plan records the parent session/run, workspace, repository and baseline identities, attempt and generation, budgets, role, stopping rules, sensitivity, tool policy, and failure policy.

Conversation delegation always creates:

- role `Explorer`;
- mode `ReadOnlyBaseline`;
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

`ModelExplorerAssignmentRunner` selects one compatible configured model and freezes that effective profile for the child. `AgentContextAssembler` includes every eligible, non-stale parent evidence item allowed by the child sensitivity policy. Repository instructions are resolved independently for the child.

`ChildAgentRequestFitter` validates the complete request against the selected model's actual context window. It never drops parent evidence, applicable `AGENTS.md` sources, or configured prompt append sources. When the complete request cannot fit, execution fails explicitly instead of silently reducing caller-provided context. The request records every delivered evidence ID.

The request does not contain:

- parent or sibling transcripts;
- hidden reasoning;
- tools absent from the exact parent request;
- mutable global model or tool state;
- evidence removed by token pressure.

Primary implementation:

- `AgentContextAssembler` selects every policy-eligible governed evidence item.
- `ChildAgentRequestFitter` validates the complete provider request and records delivered evidence IDs.
- `ChildAgentPrompt` renders instructions, assignment, evidence, tools, and output contract.
- `ChildAgentInstructions` resolves the applicable repository instruction bundle.

### 6. The child model/tool loop is explicitly bounded

`ChildAgentModelLoop` streams model output, executes validated read-only tool batches, stores each tool result as child-owned evidence, and sends a bounded tool-result continuation back to the child. One immutable tool-schema estimate is reused across continuation rounds. Wire accounting includes message content, tool-call IDs, tool names, framing, tool schemas, and output reserve.

Child exploration is claim-oriented rather than survey-oriented. The child prompt requires the model to identify the minimum externally verifiable claims in its objective, associate every tool call with an unsupported claim, batch independent inspections, prefer semantic or structural evidence, and move from discovery to exact symbols and paths once targets are known. `dotnet_inventory` is reserved for objectives that actually depend on solution, project, framework, or dependency topology; it is not a default first call for symbol or control-flow traces.

`ChildAgentEvidenceProgressTracker` seeds coverage from all delivered parent evidence and observes the exact sanitized result plus attributable sources for each child tool invocation. After every batch, the host appends an advisory progress message that distinguishes newly attributed files/source identities from merely distinct result payloads. A different payload without attributable source expansion is not represented as evidence-coverage progress. Feedback discourages equivalent repeated requests while telling the Explorer to continue with another relevant approach when a requested claim remains unsupported; one empty, noisy, irrelevant, or incomplete result is not a terminal gap. This signal is not a quota, role-independent workflow, or authority boundary: it never blocks a tool, prescribes a retrieval sequence, removes a result, drops prior messages, or replaces parent evidence, applicable `AGENTS.md`, configured prompt appends, or inherited policy.

The Explorer output policy explicitly defines `unresolvedQuestions` and `coverageNotes` as string arrays. A rejected structured finding receives a field-specific bounded correction when available. The model may make a distinct corrected attempt without a cumulative correction quota, but exact repetition of an already rejected response is a terminal non-progress cycle rather than an unbounded retry loop.

The ledger records model tokens, tool calls, evidence items, files, bytes, actions, and corrections without imposing cumulative ceilings for `delegate_agents`. Each request must still fit the selected model's real context window and provider output limit. Per-response tool request counts and argument payloads, individual tool outputs, output-schema field sizes, wall time, model text, reasoning, and the final structured tool envelope remain host-bounded. Findings have no separate count cap; the result projector retains all findings that fit the structured envelope and allocates constrained envelope space fairly across children. Exhaustion of a real request, payload, output, or deadline boundary ends the child explicitly instead of silently borrowing from siblings.

The child must return the structured finding schema. Malformed output or citations that were not actually delivered to that child receive host-authored correction feedback; model-callable exploration continues until valid output, cancellation, a real request boundary, or the child deadline.

Primary implementation:

- `ChildAgentModelLoop` owns streaming, continuations, tool batches, and resource charging.
- `ChildAgentEvidenceProgressTracker` measures distinct coverage without trimming context or enforcing a quota.
- `ChildAgentFindingParser` parses and bounds structured findings.
- `AgentBudgetLedger` enforces hierarchical resource reservations.
- `ModelWireEstimator` performs conservative provider-neutral request accounting.

### 7. Findings cross an evidence-backed join boundary

Every finding must cite at least one exact evidence ID from either:

- the fitted parent evidence rendered in the initial child request; or
- a tool result stored and rendered during that child's loop.

The child outcome carries this immutable delivered-ID set. Admission never reconstructs it from the current evidence store, so later invalidation or unrelated session evidence cannot change what the child was allowed to cite.

After the joined checkpoint is durable, `AgentFindingAdmission` validates all findings and prepares one parent-evidence batch. Evidence publication and delegation disposition share one synchronous commit gate:

- cancellation winning before the gate admits no parent evidence and records cancellation;
- join winning at the gate commits the complete evidence batch and prevents later cancellation from replacing the joined result;
- pre-commit failure leaves the parent store unchanged;
- post-commit subscriber failure or timeout cannot roll back committed evidence.

Primary implementation:

- `AgentFindingAdmission` validates identity, generation, schema, citation set, and provenance.
- `EvidenceStore.TryAddBatchAsync` owns the atomic evidence/commit gate.
- `DomainEventStream.PublishCommittedBatchAsync` prevents observers from seeing uncommitted evidence and bounds committed subscriber delivery.

### 8. The parent receives a compact projection

`DelegateAgentsResultProjector` maps the durable checkpoint to a model-facing result. `DelegateAgentDisagreementDetector` conservatively flags same-subject conflicts. `DelegateAgentsResultRenderer` enforces the final text and byte bounds.

The result contains:

- delegation and assignment IDs;
- aggregate and child statuses;
- bounded findings and confidence;
- file/symbol hints and cited evidence summaries;
- uncertainty, omissions, and disagreement signals;
- bounded token and tool-call usage.

The result excludes raw transcripts, hidden reasoning, provider payloads, raw tool JSON, mutable host objects, and uncited child claims.

## Failure and cancellation semantics

The important rule is that every path becomes inspectable terminal state:

| Condition | Result |
|---|---|
| Every child returns usable findings | `Completed` |
| At least one child is usable and another fails or is cancelled | `Partial` |
| No child returns usable findings | `Failed` |
| Parent cancellation wins before join commit | `Cancelled` |
| Queue capacity rejects admission | `Failed` with terminal child outcomes |
| Finding validation or evidence join fails | `Failed`; no partial parent evidence |
| Cancellation arrives after join commit | Joined result remains authoritative |

Cancellation is cooperative, but non-cooperative work is still observed and bounded at host-owned wait boundaries. A cancelled or obsolete generation cannot publish authoritative findings later.

## Component review map

For a focused code review, read these files in order:

1. `DelegateAgentsTool.cs`: public tool contract and four-step execution path.
2. `DelegateAgentsContracts.cs`: DTOs, limits, and strict input validation.
3. `DelegateAgentsPlanning.cs`: host-owned assignment and authority construction.
4. `DelegationOrchestration.cs`: scheduler, checkpoints, cancellation, and join arbitration.
5. `ModelExplorerAssignmentRunner.cs`: child composition and parent evidence join.
6. `ChildAgentRequestFitter.cs`: exact initial request fitting.
7. `ChildAgentModelLoop.cs`: bounded model/tool continuation loop.
8. `AgentContext.cs`: child context selection and finding admission.
9. `EvidenceStore.cs`: atomic evidence batch commit.
10. `DelegateAgentsResultProjector.cs` and `DelegateAgentsResultRenderer.cs`: final bounded output.

The remaining changed files mostly connect these authorities to application composition, persistence, terminal inspection, general tool policy, and documentation.

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
