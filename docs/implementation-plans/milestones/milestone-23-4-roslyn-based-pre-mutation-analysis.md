## Milestone 23.4 - Roslyn-Based Pre-Mutation Analysis

**Status:** See the [authoritative milestone index](../milestones.md).

**Objective:** Move feedback earlier than user interruption, build/test, and disk mutation by running host-owned plan sanity checks before plan review/auto-approval and in-memory Roslyn syntax, semantic, compilation, and trusted/isolated analyzer-code-style checks on proposed mutations before mutation approval, disk mutation, or expensive validation, then letting the model repair against host-produced focused diagnostics while still in the relevant proposal phase.

**Summary:** Adds a cheap pre-mutation Roslyn gate and bounded proposal-phase repair loop so invalid C# and many semantic/analyzer failures are corrected before exact-diff approval and authoritative build/test validation. Also plans low-friction plan approval policy plus all-plan repository sanity checks so invalid plans revise before user review and trusted valid plans can auto-approve without removing the structured plan contract.

**Deliverables:**

- Configurable plan approval policy, distinct from mutation approval policy, with interactive/headless command support and repository/session/user precedence.
- Cheap repository sanity checks for all structured plans before manual review or policy auto-approval.
- Bounded plan-revision repair for missing, ambiguous, non-existent, conflicting, or under-scoped affected files before the user sees invalid plans.
- Durable plan auto-approval provenance, risk classification, repository identity binding where relevant, and concise TUI/headless projection.
- In-memory overlay application for proposed `.cs` mutations against the current immutable mutation baseline.
- Parse-option-aware syntax diagnostics for would-be source before approval or disk writes.
- Bounded affected-document/project semantic diagnostics and `Compilation.GetDiagnostics()` fast checks without invoking `dotnet build`.
- Trusted or isolated analyzer/code-style dry runs using existing analyzer/configuration rules, with untrusted repository analyzer/source-generator checks explicitly degraded until post-approval validation.
- Proposal-phase correction loop that returns mapped diagnostics to the model before showing the user an approval diff.
- Diff-local diagnostic packets with changed hunk, location, diagnostic ID/message, severity, project/TFM, confidence, omissions, and containing symbol/member where available.
- Candidate scoring and gate decisions that stop obviously bad candidates before expensive validation while preserving normal build/test authority.
- Tool-result-aware correction guidance and narrowed repair-phase tool advertisement.
- Sanitized metrics for pre-build diagnostics caught, repair rounds, invalid proposals, repeated schema/tool mistakes, and expensive validation avoidance.
- Focused automated coverage, user-facing docs, maintained manual-test-plan updates, and DOX closeout.

**Plans:**

- [Plan 74 - Roslyn-Based Pre-Mutation Analysis](../plan-74-roslyn-based-pre-mutation-analysis.md)
- [Plan 75 - Plan Approval Policy and Sanity Checks](../plan-75-plan-approval-policy-and-sanity-checks.md)

**Exit criteria:**

- Every structured plan receives bounded repository sanity checks before manual review or policy auto-approval.
- Repairable invalid plans re-enter a bounded plan-revision loop rather than being shown to the user.
- Configured plan approval policy can auto-approve valid low-risk/trusted plans without weakening mutation approval or exact-diff gates.
- Proposed `.cs` mutations are syntax-checked through an in-memory overlay before any mutation approval prompt or repository write.
- Blocking syntax failures and available semantic/analyzer failures trigger bounded proposal-phase repair rather than immediate user approval or full build/test.
- Loaded-project semantic, fast compilation, and trusted analyzer/code-style checks run without `dotnet build` where semantic confidence permits, and degraded coverage is explicit.
- Diagnostics returned to the model are focused, bounded, mapped to changed hunks and containing symbols/members where available, and include confidence/omission metadata.
- Candidate scoring prevents clearly bad proposals from reaching expensive validation while approved candidates still go through existing exact-diff approval, transactional application, and authoritative build/test validation.
- Repair loops are bounded, cancellable, resume-safe at host boundaries, and cannot apply changes without Plan-30/37 approval.
- Observability distinguishes diagnostics caught pre-build from build/test failures without recording source contents, secrets, raw logs, or Roslyn object graphs.
- Focused validation/execution/workspace/context tests, architecture tests, user-facing docs, manual test plan updates, and DOX closeout are complete.

**Prerequisites:** Plans 06, 08-13, 18, 27, 30, 37, 40, 42-44, 48-49, 51-57, and 73.

**Scope decisions:**

- This milestone keeps structured plans mandatory as execution contracts even when plan approval is policy-authorized.
- Plan approval policy is distinct from mutation approval policy; auto-approving a plan never applies or approves an exact diff.
- Plan sanity checks are cheap host-owned repository checks, not full semantic/build/test validation.
- This milestone is a pre-mutation screening and repair feature, not a replacement for full build/test validation.
- Roslyn pre-mutation diagnostics are fast host evidence with explicit confidence and omissions; they do not become self-approval or final acceptance evidence.
- Pre-mutation analysis is read-only and in-memory: no disk writes, Git staging, restore, build, test, or repository-code execution may occur.
- Analyzer execution must reuse existing trusted analyzer/configuration boundaries and cannot load model-supplied analyzers or arbitrary command-line input.
