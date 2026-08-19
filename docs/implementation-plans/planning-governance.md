# Planning Documentation Governance

## Purpose

Keep implementation planning useful without turning every new item into a repository-wide historical-document synchronization exercise.

The planning package separates current status, executable work, durable capability contracts, acceptance behavior, and manual procedures. Each fact has one owner. Other documents link to that owner instead of restating it.

## Authority map

| Information | Sole owner | Other documents |
|---|---|---|
| Current milestone lifecycle status | `milestones.md` | Link only; never restate status |
| Current work-item status, delivery track, and prerequisites | The active implementation document | README may provide a title/link only |
| Durable capability objective, boundaries, and exit criteria | The owning milestone detail | Freeze after completion |
| Product-level acceptance behavior | `acceptance-scenarios.md` | Work items cite scenario IDs |
| Executable manual verification procedure | `manual-test-plan.md` | Work items cite MTP IDs |
| Architecture and security decisions | ADRs, guardrails, and `00-shared-context.md` | Work items cite the owning contract |
| Cross-milestone legal sequencing | `milestones/dependency-dag.md` | Work-item prerequisites stay in the work item |

## Lifecycle

Milestones use only these statuses in `milestones.md`:

- **Planned** — accepted scope exists, but implementation has not started.
- **Active** — implementation or required milestone exit work remains.
- **Complete** — the milestone exit criteria are satisfied.
- **Archived** — the milestone was retired or superseded and has no active work.

Do not encode test counts, pending environments, rehearsal notes, or work-item status in the milestone status cell. Verification procedures express what must still be exercised; the status index expresses lifecycle only.

An active implementation document declares its own status, delivery track, and prerequisites near the top. Historical implementation documents are records and do not require metadata backfills.

## Completed milestone freeze

Once a milestone becomes **Complete**:

- Treat its detail document as an immutable capability contract.
- Do not reopen it for later refactoring, cleanup, hardening, or incidental compatibility work.
- Edit it only to correct a factual error, repair a link, or resolve a contradiction with the capability as completed.
- Do not add later work-item links, completion notes, test counts, or retrospective implementation details.

Later work may depend on the completed milestone without becoming part of it.

## Maintenance track

Cross-cutting remediation, internal refactoring, hardening, documentation repair, and compatibility work that does not introduce a new product milestone belongs to the standing **Maintenance** track.

A maintenance item:

- identifies its own prerequisites and affected capability contracts;
- preserves existing milestone ownership and status;
- adds or changes acceptance scenarios only when observable product behavior changes;
- adds or changes manual procedures only when a user- or operator-executable check changes;
- never reopens a completed milestone merely because it improves that milestone's implementation.

If maintenance work grows into a distinct user capability with independent exit criteria, create a new milestone instead.

## Document rules

### README

`README.md` is navigation only. It may list implementation-document links and short capability titles. It must not contain implementation status prose, milestone status, dependency essays, completion history, or coverage summaries.

### Implementation documents

The implementation document owns its status, delivery track, prerequisites, scope, tasks, testing, and acceptance criteria. Dependencies must not be copied into the package README or acceptance scenarios.

### Milestone index and details

`milestones.md` is the only current milestone-status view. Detail files own durable capability contracts and link back to the index for current status. Completed details are frozen.

### Dependency DAG

`milestones/dependency-dag.md` contains milestone-level legal sequencing only. Work-item-level prerequisites remain in each implementation document. The DAG must not carry status, implementation history, or per-item task prose.

### Acceptance scenarios

Acceptance scenarios are product-level behavioral specifications:

- headings contain stable scenario IDs and capability names only;
- steps and `Verifies` text describe observable behavior and durable invariants;
- scenarios do not list contributing work-item numbers, milestone numbers, implementation status, coverage status, or test counts;
- implementation documents reference scenario IDs in the forward direction.

### Manual test plan

The manual test plan is an executable regression catalog:

- headings contain stable MTP IDs and behavior names;
- cases contain prerequisites, steps, expected results, and safety-relevant limitations;
- it does not contain implementation status, milestone status, work-item attribution, completion narratives, automated-test counts, or a global roadmap baseline;
- retain case IDs; mark obsolete cases `Retired` and link their replacement.

### Shared context

`00-shared-context.md` contains durable architecture, boundary, and implementation-template guidance. It must not contain roadmap inventories, work-item status, milestone status, or historical feature-to-work-item matrices.

### DOX

Planning progress does not trigger changes to repository or product-subtree `AGENTS.md` files. Update DOX only when durable folder ownership, implementation guidance, workflow, or child indexes change.

## Minimal update matrix

| Change | Required planning-document updates |
|---|---|
| Add maintenance work | New implementation document; one README navigation row |
| Change active work status or prerequisites | Active implementation document only |
| Add a milestone | Milestone index row; new detail; milestone DAG edge; README navigation when work documents are added |
| Change milestone lifecycle status | `milestones.md` only |
| Change observable acceptance behavior | Owning scenario; affected implementation document |
| Change an executable manual workflow | Owning MTP case; affected user/operator documentation |
| Complete a milestone | `milestones.md` only after exit criteria pass |
| Refactor completed capability internals | Maintenance implementation document; no historical milestone edits |

## Verification

Before completing a planning-document change:

```powershell
rg -n "^## Scenario .*\*\(.*plan|^\*\*(Coverage status|Planned coverage):" docs\implementation-plans\acceptance-scenarios.md
rg -n "^\*\*(Status|Baseline|Coverage status|Planned coverage):|^## MTP-.*\(M[0-9]" docs\implementation-plans\manual-test-plan.md
rg -n "Implementation status:|implementation-complete|completion history" docs\implementation-plans\README.md
rg -n "Plan [0-9]|plan-[0-9]" -g "AGENTS.md" .
git diff --check
```

The first four searches must return no prohibited bookkeeping matches. Literal repository artifact names used in valid commands are not historical attribution.
