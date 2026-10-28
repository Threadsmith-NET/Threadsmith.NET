# Shared context for ordinary delegation

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Existing model-requested delegation, frozen child assignments, captured PR evidence delivery, and the maintained review skill.

## 1 Objective

Let the lead generate common handoff text once while preserving each specialist's focused assignment and received context.

## 2 Architectural Context

Extend the existing [delegation contract](../architecture/delegate-agents-tool.md); retain the plan factory, scheduler, context assembler, child prompt, tool pipeline, and join path.

## 3 Scope

Optional top-level `sharedContext`, optional individual `context`, host expansion before assignment freeze, review-skill adoption, documentation, and regression coverage. The review handoff references captured PR inventories instead of reproducing their complete path lists; reviews without captured inventories retain explicit lists. Align the TestReviewer system prompt with its assigned execution ownership: run relevant tests, evaluate observed results, and report execution evidence and limits while preserving the Markdown headings, lists, and JSON example.

## 4 Non-Scope

No new execution path, transcript inheritance, evidence store, role, authority, concurrency policy, result format, or child-input caching mechanism. No claim that total run tokens or delegation latency have decreased without a live comparison.

## 5 Current State

The prior tool required context for each child, and the review skill required repeating common target, preservation, and response instructions five times. A captured run contained a 33,871-character delegation call and a roughly 127-second preparation round; these are observations of that run, not a general benchmark.

## 6 Proposed Design

Trim both context fields, join shared text before individual text with a blank line, and store the expanded result in the existing immutable assignment. Require non-whitespace text in at least one field for every child. Include expansion in the existing onboarding brief limit. Keep common review handoff material in `sharedContext` and role-specific objectives in the five tasks.

## 7 Public Contracts

`DelegateAgentsInput.SharedContext` and `DelegateAgentRequest.Context` are optional nullable strings. Existing individual-context calls retain their behavior. Strict provider projection supports null for optional fields.

## 8 Project/File Changes

Execution owns the DTO, schema, validation, expansion, and delegation description. Skills owns the review prompt. ParallelAgents and Skills tests cover the contract. Architecture, operations, and prompt-reference documents describe the shipped behavior.

## 9 Ordered Tasks

1. Extend input/schema and freeze combined context through the existing plan factory.
2. Update delegation and review prompts with shared handoff guidance.
3. Exercise compatibility, transport, persistence, rendering, isolation, and brief limits.
4. Validate the final solution and documentation.

## 10 Testing

Run solution restore, Debug build, and solution tests using the contributor commands, plus the packaged-documentation preflight and changed-file analyzer checks. Cover legacy calls, shared-only and mixed contexts, invalid missing context, strict schema projection, pipeline checkpoints, untrusted prompt framing, sibling isolation, and the onboarding brief boundary. Scenario L owns the acceptance behavior. A comparable live Qwen run remains necessary to measure size and latency improvements; macOS/Linux and physical-terminal behavior require their own evidence.

Validation commands:

```powershell
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --project tests/Threadsmith.ParallelAgents.Tests/Threadsmith.ParallelAgents.Tests.csproj --configuration Debug --no-build
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Execution/DelegateAgentsContracts.cs src/Threadsmith.Execution/DelegateAgentsPlanning.cs src/Threadsmith.Execution/DelegateAgentsTool.cs tests/Threadsmith.ParallelAgents.Tests/DelegateAgentsToolExecutionTests.cs tests/Threadsmith.ParallelAgents.Tests/DelegateAgentsToolExecutionTests.SharedContext.cs tests/Threadsmith.ParallelAgents.Tests/Plan91DelegationToolTests.Intelligence.cs tests/Threadsmith.Skills.Tests/SkillSubsystemTests.NativeReview.cs
git diff --check
```

Windows validation: restore passed; final build passed with zero warnings/errors; the focused delegation suite passed 335 tests with one skipped; the final solution suite passed 4,689 tests with 64 skipped and zero failures (4,753 total). Packaged-documentation validation and `git diff --check` passed. Analyzer verification returned exit code 2 for 29 pre-existing suggestions on unchanged lines: one RCS1085 in `DelegateAgentsTool.cs`, and 14 each of CA1826/RCS1246 in `DelegateAgentsToolExecutionTests.cs`. No suggestions remain in the new shared-context code. Live-provider, physical-terminal, and macOS/Linux checks were not run. Evidence is in ignored `.inbox/shared-context-*` logs; final results supersede the intermediate failed build and test attempts.

Inventory-reference follow-up: the review prompt and existing contract test now require complete captured scope by reference, allow individual focus paths, and retain explicit inventories without captured evidence. The same restore, solution build/test, documentation, and diff checks passed again on Windows: zero build warnings/errors, 4,689 tests passed, 64 skipped, zero failures. The app output's review prompt matches the source SHA-256. `dotnet format analyzers tests/Threadsmith.Skills.Tests/Threadsmith.Skills.Tests.csproj --verify-no-changes --no-restore --severity info --include tests/Threadsmith.Skills.Tests/SkillSubsystemTests.NativeReview.cs` passed without diagnostics. Evidence is in ignored `.inbox/review-inventory-reference-*` logs. Live model adoption of the inventory-reference wording and macOS/Linux remain unverified.

TestReviewer execution follow-up: removed the role's conflicting test-execution prohibition. It now runs assigned relevant checks through available tools, evaluates observed output and exit codes, reports commands and results, and respects target/environment and checkout-preservation limits. The five Markdown headings retain their order and have blank-line separation; the fenced JSON example parses successfully. The built prompt matches the source SHA-256. The same restore, solution build/test, documentation, and diff checks passed on Windows: zero build warnings/errors, 4,689 tests passed, 64 skipped, zero failures. Evidence is in ignored `.inbox/test-reviewer-execution-*` logs. No C# or execution-path changes were needed. Live model adoption and macOS/Linux remain unverified.

## 11 Security/Permissions

Shared context remains caller-authored untrusted task data in the existing user assignment message. It cannot grant tools, evidence access, trust, or model routing. Existing escaping, request capacity admission, evidence ownership, and cancellation remain effective.

## 12 Observability

Ordinary tool arguments record the shared field; frozen assignments/checkpoints and child requests contain expanded text through existing logging and persistence. No new telemetry channel.

## 13 Migration/Compatibility

No durable schema migration: persisted assignments still use `InitialContext`. Existing callers can continue supplying individual context without the new field.

## 14 Acceptance Criteria

Every child receives shared text once followed by only its own context, through ordinary policy and execution. Empty effective context is rejected. The review skill retains all five roles, evidence references, preservation rules, exclusive test ownership, and reporting instructions.

## 15 Risks

Models may continue repeating shared text despite guidance. Each child still pays its own context cost. A live run must assess improvement without weakening review coverage.

## 16 Documentation

Update delegation architecture and operations, both prompt catalogs, and Scenario L. No manual workflow changes; existing delegation procedures remain applicable. Completed milestone contracts remain unchanged.

## 17 Open Decisions

None for implementation. Live Qwen measurement is a follow-up validation of the expected performance benefit.
