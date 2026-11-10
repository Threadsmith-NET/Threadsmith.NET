# Bounded pre-edit compiler checks

**Status:** Complete
**Delivery track:** Maintenance
**Prerequisites:** Existing direct-edit writer, versioned candidate analysis, semantic activity projection and provider-boundary feedback delivery.

## 1 Objective

Observe candidate compiler checks for a configurable five seconds before writing; reject completed reported errors or warnings and preserve asynchronous admission on expiry.

## 2 Architectural Context

Reuse `SourceEditApplication`, the exact authorized snapshot, the semantic engine's existing candidate task, the effect journal and sole transactional writer. Preserve existing analyzer and post-edit feedback ownership.

## 3 Scope

Add `limits:semantic:editAnalysis:preEditCheckWaitMilliseconds`, default `5000`; zero preserves advisory-only admission. Return non-applied receipts with diagnostic evidence for completed failing candidates.

## 4 Non-Scope

No full build, new diagnostic pass, warning-policy expansion, analyzer gate, new correction loop, Git publication or change to final-feedback deadlines.

## 5 Current State

Candidate analysis has a short observation allowance and never rejects an authorized edit on compiler findings. Its existing activity already owns running status and duration.

## 6 Proposed Design

Pass the separate pre-edit allowance to the existing compiler observation. Reject current, completed reported errors or selected warnings before capturing write intent. Observation expiry returns pending evidence even if completion races the deadline, without cancelling the underlying candidate. Unavailable or obsolete evidence preserves existing admission. Keep analyzer evidence separate. Share compiler finding severity with presentation.

## 7 Public Contracts

The new nonnegative millisecond setting is startup-captured through ordinary configuration. Existing `NotApplied` receipts carry feedback and remain idempotent; revised instructions use a new invocation. Existing DTO and event schemas remain compatible.

## 8 Project/File Changes

Core options and diagnostic severity policy; execution admission and prompt wording; semantic observation; shared presentation and exact-review completion; terminal non-write persistence; configuration examples; focused mutation, semantic and configuration tests; maintained documentation.

## 9 Ordered Tasks

Implement configuration and observation, add rejection through the existing effect journal, verify timeout/cancellation/replay and model-facing feedback, update owned documentation, then validate the final revision.

## 10 Testing

Barrier-controlled mutation admission, compiler errors/warnings including bounded detail lists, clean and advisory fallback, separate analyzer findings, cancellation, durable rejection/replay and ordinary tool feedback. A real semantic-engine test advances an observed fake-clock deadline and proves retained candidate completion and committed reuse. Configuration tests cover binding, default and invalid values.

Final Windows verification (2026-11-10):

```powershell
dotnet restore src/Threadsmith.sln
pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
$env:THREADSMITH_TEST_RIPGREP = Join-Path $PWD 'artifacts/test-ripgrep/stage/tools/rg.exe'
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
git diff --check
```

Restore, asset staging, build and documentation preflight passed; build had zero warnings/errors. The final full suite passed 5,502 tests, with 78 platform/live/explicit-measurement skips and zero failures. Automated coordinator coverage verifies that an approved review rejected by compiler feedback continues to the model without writing or ending the frontend. Existing bounded-rendering tests also pass.

The following focused informational analyzer verification passed:

```powershell
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Core/SourceEditAnalysisPolicy.cs src/Threadsmith.Core/EditAnalysisOptions.cs tests/Threadsmith.Architecture.Tests/EditAnalysisConfigurationTests.cs tests/Threadsmith.ModelTooling.Tests/SourceEditAnalysisTests.PreEditWait.cs tests/Threadsmith.Mutations.Tests/SourceEditApplicationTests.PreEditChecks.cs
```

A broader informational check used this command:

```powershell
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Core/EditAnalysisOptions.cs src/Threadsmith.Core/SourceEditAnalysisPolicy.cs src/Threadsmith.DotNet/SemanticEngine.EditAnalysis.cs src/Threadsmith.Execution/SourceEditApplication.cs src/Threadsmith.Interaction/Coordination/InteractionController.cs src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs src/Threadsmith.Interaction/Presentation/InteractionPresentationFormatter.cs src/Threadsmith.Persistence/ExecutionCheckpointStore.Effects.cs tests/Threadsmith.Architecture.Tests/EditAnalysisConfigurationTests.cs tests/Threadsmith.ModelTooling.Tests/SourceEditAnalysisTests.PreEditWait.cs tests/Threadsmith.Mutations.Tests/SourceEditApplicationTests.PreEditChecks.cs tests/Threadsmith.Mutations.Tests/SourceEditApplicationTests.Interaction.cs
```

It exited 1 for existing informational suggestions in unchanged portions of those files: RCS1146, RCS1205, CA1869, RCS1213, RCS1111 and IDE0032. Their source was checked against the existing revision; unrelated cleanup was excluded. macOS/Linux execution, live providers and real-terminal timing were not assessed locally. No Git publication or remote CI run was requested.

## 11 Security/Permissions

Trust, exact review, confinement, source preconditions and writer recovery remain authoritative. No new write authority or durable write intent is created for a rejected candidate.

## 12 Observability

Reuse correlated semantic start/completion events and the existing activity/timer. Return explicit non-applied detail and diagnostics to the model. No new renderer or event stream.

## 13 Migration/Compatibility

Omitted configuration enables the default five-second window. Zero restores previous admission. Expiry retains the previous asynchronous path, including candidate promotion and later feedback waits. Diagnostic warning selection and exclusions remain unchanged.

## 14 Acceptance Criteria

Scenario AK (incremental compiler feedback) and MTP-162 cover rejection, repair, timeout fallback, analyzer independence and cancellation. No write occurs before candidate observation completes or expires; completed reported errors/warnings reject without effects.

## 15 Risks

Pre-existing reported findings can reject a candidate; related repairs may need a single grouped edit. Diagnostics remain scoped and filtered. Missing coverage is not a clean build claim. Cold checks can outlast the window and retain advisory behavior.

## 16 Documentation

Update mutation ownership, conversation flow, user guidance, resource limits and both configuration examples. Preserve completed milestone contracts.

## 17 Open Decisions

None. The requested timeout behavior is explicitly advisory fallback, not fail-closed admission.
