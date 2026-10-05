# Conversation and source editing

Threadsmith runs one ordinary model conversation. The model inspects relevant source, chooses an edit sequence, invokes advertised tools, and uses their results to continue the task. The host owns capability admission, trust, exact authorization, transactions, cancellation, durable effects and validation.

```mermaid
flowchart TD
    U[User request] --> C[Bounded conversation context]
    C --> M[Model response]
    M --> T[Central tool pipeline]
    T --> R[Read or semantic query]
    R --> C
    T --> E[edit_source]
    E --> A[Exact diff authorization]
    A --> W[Existing transactional writer]
    W --> F[Write receipt and advisory compiler feedback]
    F --> C
    M --> D[Ordinary final answer]
    T --> V[Model-requested build or test]
    V --> C
    D --> O[Cumulative disk outcome]
```

`edit_source` accepts a rationale and an ordered list of create, replace, delete, move or supported semantic-rename operations. Host identities are assigned by the application. Supporting reads are independent of the write set. The existing scheduler preserves dependent tool order and the workspace edit lease serializes writes.

Enabled tools are advertised according to current trust and tool allow/deny policy, without phase, side-effect, or conversation opt-in filtering. Required approval is enforced at invocation. The tool pipeline retains policy, hooks, activity events, resource bounds and cancellation. The edit application captures current touched endpoints, resolves exact anchors, stages the diff and applies the surviving mutation approval policy. A declined edit returns a denied receipt; a changed source precondition returns a conflict requiring a fresh read. Neither outcome silently applies changes.

After authorization, the semantic owner analyzes the candidate within a bounded allowance. Compiler errors are advisory: well-formed authorized edits can temporarily introduce syntax or semantic errors. The effect journal persists exact before/after endpoint identities before invoking the existing writer. The writer rechecks preconditions and verifies final bytes. Disk outcome and semantic outcome remain separate.

Matching candidate results are reused after commit. Broader owner/dependent analysis continues through the existing compiler coordinator with generation fencing and bounded coverage. New diagnostics enter the ordinary conversation at a provider-compatible boundary; already delivered tool results are preserved. Missing or obsolete analysis is reported explicitly.

The ordinary final response ends model continuation and records cumulative disk effects; it does not launch validation. The model decides when to invoke build and test tools, with guidance to resolve incremental compiler errors and obtain current feedback first. Advisory compiler feedback never establishes build or test success. Cancellation or a later provider failure preserves already proven disk effects in the durable outcome.

No executable plan, plan approval, step, tranche, proposal-generation turn or completion tool is required. A user-requested written plan is ordinary advisory conversation text.

See [mutation model](../architecture/mutation-model.md), [execution recovery](execution-resumption.md), [semantic refresh](semantic-refresh.md), and [validation pipeline](../architecture/validation-pipeline.md).
