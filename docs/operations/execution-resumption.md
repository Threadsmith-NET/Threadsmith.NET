# Execution orchestration and resumption

An approved implementation plan continues in the same run. Approval authorizes the plan only; it does not authorize repository writes. Before approval, every proposed plan passes cheap host-owned sanity checks; repairable plan-scope failures return to the model for bounded plan revision instead of creating a review prompt.

## Normal flow

1. The model proposes a structured plan through `propose_plan`.
2. The host runs plan sanity checks over structured schema-2 file intents, path confinement, current baseline existence, protected/secret/Git targets, generated/binary risk, lifecycle/configuration/dependency/test-deletion risk, and scope bounds.
3. Repairable sanity failures are recorded and fed back as plan-revision evidence. Non-repairable path/trust/protected-policy failures fail closed.
4. A passing plan is manually approved or policy-auto-approved according to `/plan-policy` / `planning:approvalPolicy`; auto-approval records policy, risk, revision, and bounded scope summary.
5. The host enters implementation, emits visible mutation-preview preparation status, and advertises eligible bounded read-only tools plus `propose_mutations`.
6. The proposal is validated against approved plan steps, paths, trust, budgets, and the current mutation baseline. C# `RenameSymbol` proposals are expanded through the semantic mutation engine before staging when semantic state is available.
7. Proposed `.cs` mutations are applied to an in-memory overlay and screened by Roslyn before staging or approval. Blocking syntax/fast compilation diagnostics return to the model for proposal repair; repository files remain unchanged.
8. The full set stages atomically only after proposal validation and pre-mutation cheap gates pass. The exact diff is recorded and shown before policy evaluation.
9. A prompted or host-policy mutation decision is recorded separately.
10. The host builds or semantically evaluates the exact pre-mutation workspace according to configured post-approval `validation:stages` and durably records `BaselineCapture`.
11. A write-ahead commit intent is recorded, the transaction is applied once, and its result is reconciled.
12. Configured post-mutation validation runs in order: semantic, compile, diagnostics, and/or affected tests. Build/test validation remains authoritative when configured.
13. The host records a terminal outcome derived from authoritative artifacts and validation evidence.

Headless callers must provide a policy-authorized decision or explicit host input. They never self-approve.

## Cancellation

Cancellation before application leaves repository bytes unchanged. Cancellation around a lifecycle commit reconciles exact endpoint identities: a fully untouched or compensated set records the operation as `RolledBack`, while conflicted or indeterminate effects record recovery-required state. Cancelled build/test output is not admitted as authoritative.

## Explicit resume

Use the shared `ResumeRunCommand` (the interactive `/resume` surface may call the same command) only for a nonterminal interrupted or cancelled execution. Resume revalidates session/run/workspace identity, plan revision and hash, diagnostic baseline, continuation artifact integrity, and operation state.

After revalidation, an interrupted pre-write baseline capture or a cancelled lifecycle commit with proven compensation returns to mutation approval so fresh authorization is required before retrying. A checkpoint proving mutation application advances by rerunning validation and outcome assembly only; it never reapplies the committed mutation. A resumed validation failure may stage a new correction for the same exact-diff review loop.

Resume is denied when:

- the execution is terminal;
- checkpoint or artifact schema is unsupported;
- a required artifact is absent or corrupt;
- repository, solution, plan, plan-approval provenance, trust, mutation policy, or baseline identity changed;
- a pending side effect cannot be reconciled safely.

A denial does not adapt or merge repository state. Start a fresh request/plan or perform explicit recovery.

## Durable evidence

Checkpoint rows contain bounded host-owned DTOs and artifact references. Plan sanity summaries, auto-approval provenance, diffs, continuation state, baseline captures, and validation evidence are sanitized and content-addressed. Terminal outcomes retain the cumulative applied diff across corrections and mark only plan steps explicitly correlated to applied proposals; untouched approved steps remain uncompleted. Hidden reasoning, secrets, raw provider payloads, and unbounded process output are never checkpoint content.
