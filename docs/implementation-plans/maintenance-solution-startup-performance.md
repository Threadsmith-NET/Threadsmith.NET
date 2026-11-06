# Reduce solution startup work

**Status:** Proposed.
**Delivery track:** Maintenance.
**Prerequisites:** The existing repository, transaction, and semantic services.

Before code changes, read these documents:

1. Read [planning governance](planning-governance.md).
2. Read [shared context](00-shared-context.md), section G.
3. Read the C# guardrails.

## 1 Objective

Reduce the time to load a large solution. Keep the existing checks for file changes, permissions, and recovery.

This plan covers the first four items from the startup review:

1. Reuse a valid restore result.
2. Read recovery bytes only when an operation needs them.
3. Reduce repeated document reads and hash calculations.
4. Reuse directory lists when the host installs file watchers.

The [host startup plan](maintenance-host-startup-performance.md) covers the last three items. You can implement either plan first.

## 2 Architectural Context

Source revision: `993ec17a22527ed2514edf6409e0a26d3c4e502c`.

| Component | Responsibility |
|---|---|
| `RepositoryLifecycle` | Selects the solution and starts restore. |
| `TransactionalWorkspace` | Keeps the original file content for changes and recovery. |
| `SemanticEngine` | Loads source code into Roslyn. |
| `SemanticRefreshCoordinator` | Checks disk changes before the host uses semantic results. |
| `RepositoryFileObserver` | Reports file changes through the existing watchers. |

Use these components for the changes. Do not add another component with the same responsibility.

The engine already compiles a small initial set of projects. It prepares the remaining projects in the background. Requests use the same preparation work.

Keep this compilation design. Keep the restrictions in [semantic validation reconciliation](maintenance-semantic-validation-reconciliation.md).

## 3 Scope

Change the work for restore, file copies, document reads, and directory scans.

Include these startup cases:

- Startup with an interactive interface.
- Startup without an interactive interface.
- Selection of a project file.
- Selection of a solution file.
- A second open of the same repository.

Use these technical terms throughout this plan:

| Term | Meaning |
|---|---|
| Restore | The .NET operation that supplies package dependencies and project files for a build. |
| Baseline | The recorded file state against which the host checks a proposed change. |
| Hash | A value calculated from content to detect a change. |
| Recovery bytes | The original file content that the host needs to undo a change. |
| Generation | One version of a workspace or its associated state. |
| Reconciliation | A comparison of recorded state with current disk state. |
| File watcher | A component that reports file or directory changes. |
| Semantic readiness | The state in which the host permits requests that need compiler information. |

## 4 Non-Scope

Keep the existing rules for project selection, compiler confidence, and analyzer use. Keep all mutation approval checks.

Do not exclude test projects to reduce startup time. Do not disable reconciliation or path checks. Do not change MSBuild or NuGet versions.

## 5 Current State

The review examined source code. The review did not measure startup time.

| Method or component | Current behavior |
|---|---|
| `HandleAsync(SelectSolutionCommand)` | Starts restore when build trust and a .NET environment permit it. This includes a second open without file changes. |
| `RestoreAsync` | Starts `dotnet restore` for the selected solution or project. |
| `HandleAsync(RecordBaselineCommand)` | Finds approved files and calculates their hashes. It also reads recovery bytes when the mutation coordinator needs them. |
| `TransactionalWorkspace` | Keeps baseline content and controls baseline replacement. |
| `GetLoadedDocumentsAsync` | Reads document text and calculates hashes. It reads each shared path once. |
| `BindCoreAsync` | Installs watchers and checks document content against disk content before semantic readiness. |
| `StartWatching` | Scans directories before and after watcher installation. Repeated calls can repeat these scans. |

Baseline capture already limits the number of simultaneous reads. Baseline capture can run at the same time as semantic loading.

Less baseline work can reduce memory use. It might not reduce total startup time.

## 6 Proposed Design

### 6.1 Measure the work

1. Add measurements to the existing startup metrics.
2. Measure each operation separately.
3. Record the time to the first display.
4. Record the time to semantic readiness.
5. Record the time to the first successful semantic request.

Measure these operations:

- Restore.
- Baseline capture.
- MSBuild load.
- Document reads.
- Reconciliation.
- Watcher installation.

Record these counts:

- File reads and bytes read.
- Bytes retained in memory.
- Directory visits and installed watchers.
- Restore processes.

Use three conditions for comparison:

- Startup without reusable state.
- A second open without file changes.
- A second open after a project change.

Record the machine, operating system, SDK, storage type, and solution size. Record the network conditions and available packages.

### 6.2 Reuse a restore result

Keep restore execution in `DotNetEnvironmentResolver`.

1. Add a record for the last successful restore to the existing repository state.
2. Set a size limit for the record.
3. Record the solution, project graph, SDK, and restore properties.
4. Record the inputs that can affect restore.
5. Check the required output files.
6. Check the required packages.
7. Check current trust before reuse or execution.

Include these inputs:

- Imported build files.
- Package configuration.
- Package lock files.
- Supported environment inputs.

Use the inputs from project evaluation when available. A solution hash alone cannot prove that restore remains valid.

If an input is unknown, run the existing restore operation. If package versions depend on external state, require proof before reuse.

Do not use timestamps alone as proof. The presence of `project.assets.json` alone is insufficient.

If all required checks pass, reuse the restore result. If a check fails, run the existing restore operation.

If restore fails or the user cancels it, do not record success. Keep explicit restore commands and their permission checks.

If workspace load detects missing restore output, mark the restore record invalid. Permit one restore attempt under current permissions. Then permit one load retry.

Report the retry through the existing status system.

### 6.3 Read recovery bytes later

1. Find every caller that uses `WorkspaceBaseline` or `TransactionalWorkspace`.
2. Record the data that each caller needs.
3. Separate baseline hashes from recovery bytes inside the existing transaction component.
4. Keep the initial file list and hashes.
5. Read recovery bytes before the first operation that needs them.
6. Compare the new bytes with the original baseline hash.

If the hashes differ, use the existing conflict or baseline replacement procedure. Do not accept the new bytes as original content.

Store recovery bytes before the host writes a file. Apply this rule to edits, file deletion, file renaming, and changes to multiple files.

Keep recovery after a crash or restart. Keep the existing limits for total bytes and simultaneous reads.

This first change reduces retained bytes. It does not eliminate the initial read for each hash.

Consider a later change to delay initial hashes or baseline registration. First check every dependent operation against the existing baseline contract.

Check these operations:

- Change preview.
- User approval.
- Detection of external changes.
- Recovery.

If the later baseline changes these contracts, keep the initial hashes. Record this restriction in the completion report.

### 6.4 Reduce document reads

Use the existing engine and refresh coordinator to compare content.

1. Identify repeated reads within one load operation.
2. Record which workspace generation produced each result.
3. Record the normalized path and document kind.
4. Record how the component represents the content.
5. Reuse a result only when these records prove that the required content matches.

A hash of raw bytes can differ from a hash of decoded text. Keep encoding, byte-order marks, and line endings in these comparisons.

If a check needs only a hash, avoid another full copy of the text. Process documents in batches with a fixed size limit.

After reconciliation uses temporary text, release the text.

If current content cannot be proved, keep the disk read. Reduce temporary memory use around that read.

File length alone is insufficient proof. A timestamp alone is insufficient proof. An empty watcher queue is insufficient proof.

Keep checks for edits during MSBuild load and watcher installation. Reject results from an old generation.

Report semantic readiness only after the required reconciliation.

After repeated work decreases, measure simultaneous reads within existing resource limits. Keep sequential reads if simultaneous reads do not improve performance.

### 6.5 Reuse directory lists

1. Keep one directory list in the existing binding component for each generation.
2. Keep the list immutable after creation.
3. Reuse the list only where path rules and required observation times match.
4. Install watchers through `RepositoryFileObserver`.
5. Check for directory changes after installation.

Keep the rules for approved roots, prohibited paths, letter case, and symbolic links. A directory list does not grant file access.

Keep permitted input directories that normal discovery filters exclude.

If a reported change is certain, create an updated list. If watcher events overflow or directory state is uncertain, scan again.

Scan again after a policy change or repository replacement. Discard the old list after cancellation or disposal.

## 7 Public Contracts

Keep current commands and event order. Keep the meaning of semantic readiness.

Use host-owned records for new state. Keep Roslyn types inside components that already use Roslyn.

Give persistent records a format version when necessary. If the host cannot read a record version, use the existing uncached operation.

## 8 Project/File Changes

Inspect these files before changes:

- `src/Threadsmith.Workspaces/RepositoryLifecycle.cs`
- `src/Threadsmith.Workspaces/DotNetEnvironmentResolver.cs`
- `src/Threadsmith.Workspaces/BaselineFileSnapshot.cs`
- `src/Threadsmith.Workspaces/TransactionalWorkspace.cs`
- `src/Threadsmith.DotNet/SemanticEngine.cs`
- `src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs`
- `src/Threadsmith.DotNet/RepositoryFileObserver.cs`
- `src/Threadsmith.DotNet/SemanticLifecycleObserver.cs`
- `src/Threadsmith.DotNet/SemanticLoadMetrics.cs`
- `src/Threadsmith.Core/SemanticStartupProgress.cs`

Change storage contracts only if the restore record requires a change. Use the existing repository lifecycle, mutation, model tooling, and architecture test projects.

## 9 Ordered Tasks

1. Compare current source with the source revision in section 2.
2. Exclude work that the current source already completes.
3. Trace both startup modes.
4. Record baseline callers and their required data.
5. Measure startup before code changes.
6. Implement restore reuse with the checks in section 6.2.
7. Implement later recovery reads with the checks in section 6.3.
8. Reduce document reads as specified in section 6.4.
9. Reuse directory lists as specified in section 6.5.
10. Test each change separately.
11. Measure the combined changes.
12. Review permissions, cancellation, recovery, and semantic readiness.
13. Record results and remaining work in this plan.

## 10 Testing

These tests apply to implementation. The plan-writing task did not run builds or tests.

| Area | Required test conditions |
|---|---|
| Restore | Open again without changes. Change an import, SDK, or package configuration. |
| Restore recovery | Delete output or a required package. Use unknown inputs. Cancel restore. Revoke trust. |
| Baseline | Change a file before capture. Keep the same file length. Add, delete, or rename a file. |
| Baseline recovery | Apply multiple changes. Exceed a limit. Undo a change. Restart after an interrupted write. |
| Documents | Share a file between projects. Change encoding. Edit during load or after a snapshot. |
| Document recovery | Delete a file. Change analyzer configuration. Complete work from an old generation. |
| Watchers | Create or rename a directory during installation. Overflow the event queue. Change a symbolic link. |
| Path rules | Use a case-sensitive root or denied path. Dispose the workspace. Open the repository again. |

Use controlled test barriers to test operation order. Use fake process results where process timing is irrelevant.

Keep real filesystem tests for platform behavior. Do not use fixed delays as proof of operation order.

Use MTP-275 for the large-solution procedure. Keep Scenario B, Scenario J, and Scenario AQ valid.

Before implementation completion, run the applicable checks in `CONTRIBUTING.md` and current CI workflows. Record exact commands and results.

Report unavailable platform checks separately.

## 11 Security/Permissions

Check trust and path permissions before access. Do not execute repository configuration.

Pass cancellation tokens through each asynchronous call. Use the existing bounded cancellation method for compiler operations that cannot stop immediately.

Discard results from cancelled or replaced workspaces.

## 12 Observability

Use existing diagnostics to report these facts:

- Whether the host reused restore results.
- Why the host ran restore again.
- How many recovery bytes the host read later.
- How many documents the host read.
- Why the host repeated a directory scan.

Use a fixed set of metric labels. Keep source content, secrets, and full path lists out of telemetry.

Report initial readiness separately from background compilation.

## 13 Migration/Compatibility

Keep support for repositories without a restore record. Run normal restore when a record is invalid.

Keep existing sessions and transaction recovery valid. Add a storage migration only if the persistent format changes.

Do not treat an old complete baseline as a partial baseline.

## 14 Acceptance Criteria

- A test with valid unchanged restore inputs starts no restore process.
- Invalid or uncertain restore inputs cause normal restore.
- Recovery bytes match the original hashes before a write.
- A write cannot start without the recovery bytes that the transaction requires.
- The implementation report documents proof that each reused result contains current content.
- Uncertain content still receives a disk check.
- Watcher installation detects directory changes during installation.
- Watcher overflow causes reconciliation.
- Tests count less work for each implemented improvement.
- Both startup modes keep current permissions, cancellation, and readiness checks.

Use identical inputs for repeated performance measurements. Report median values and ranges.

Set acceptable time and memory limits before comparison. Include startup without reusable state and the first operation after startup.

Report any work that moves from startup to the first operation.

## 15 Risks

External state can change restore requirements. If the host cannot prove reuse is safe, run restore.

Later recovery reads can delay the first edit. Measure that delay separately.

Simultaneous disk reads can increase total time. Reused directory lists can miss changes. Keep resource limits and reconciliation.

## 16 Documentation

Keep this plan in the planning index. Record implementation results here.

Keep completed milestone contracts unchanged. Change operator documentation only when diagnostics or controls change.

Change acceptance scenarios only when observable behavior changes.

Writing reference: [asd-ste100 skill](https://github.com/danyuchn/asd-ste100-skill/blob/master/SKILL.md).
The rewrite uses the skill's structural rules. The rewrite does not establish compliance with the official dictionary.

## 17 Open Decisions

| Decision | Required action until resolved |
|---|---|
| Which restore inputs permit reuse? | Run normal restore when proof is incomplete. |
| Which callers need original bytes at startup? | Keep immediate capture for those callers. |
| Can initial hashes move to a later operation? | Keep initial hashes until all baseline contracts remain valid. |
| Which document reads can use earlier results? | Keep the disk read without proof of current content. |
| Which performance limits apply? | Set limits before the comparison. |
