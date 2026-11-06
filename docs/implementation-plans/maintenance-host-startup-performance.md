# Reduce host startup work

**Status:** Proposed.
**Delivery track:** Maintenance.
**Prerequisites:** The existing storage, repository, provider, MCP, and shutdown services.

Before code changes, read these documents:

1. Read [planning governance](planning-governance.md).
2. Read [shared context](00-shared-context.md), section G.
3. Read the C# guardrails.

## 1 Objective

Reduce the time to the first usable display. Keep required security and storage checks before dependent operations.

This plan covers the last three items from the startup review:

5. Check only new or changed storage data when previous checks remain valid.
6. Avoid repeated setup for the initial repository.
7. Let optional connections complete after the first display.

The [solution startup plan](maintenance-solution-startup-performance.md) covers the first four items. You can implement either plan first.

## 2 Architectural Context

Source revision: `993ec17a22527ed2514edf6409e0a26d3c4e502c`.

| Component | Responsibility |
|---|---|
| `Program` | Creates host services before it starts the shell. |
| `HostFoundation` | Prepares storage. |
| `ModelComposition` | Prepares model providers. |
| `IntegrationComposition` | Prepares MCP connections. |
| `ApplicationComposition` | Creates application services and connects them to a repository. |

Use these components for the changes. Keep existing events and shutdown control.

Do not add another startup scheduler. Do not put service lifetime control in the user interface.

## 3 Scope

Limit storage reads. Reuse valid audit results. Avoid repeated repository setup.

Let optional provider work and MCP connections continue after the first display.

Include these cases:

- Startup with an interactive interface.
- Startup without an interactive interface.
- A change to another repository.
- Restart.
- Failure.
- Cancellation.

Use these technical terms throughout this plan:

| Term | Meaning |
|---|---|
| Audit | A check of stored data for secrets that need redaction. |
| Redaction | Replacement of secret content with safe text. |
| Sanitizer | The component that replaces secret content. |
| Artifact | Content that the host stores separately from event rows. |
| Checkpoint | A stored record of audit work that completed successfully. |
| Retention | The rules that control how long stored data remains available. |
| Binding | The connection between application services and one repository. |
| Binding record | Evidence that the host completed a binding with specified inputs. |
| Catalog | A list of available models, skills, or tools. |
| MCP | Model Context Protocol, which connects the host to external tools and resources. |
| Hook | An operation that the host invokes at a specified lifecycle event. |

## 4 Non-Scope

Keep redaction and retention limits. Keep schema migrations and policy checks.

Do not start untrusted connections. Do not change authentication permissions or secret precedence.

Do not add automatic extension loading. Do not delay required policy hooks.

Keep embedding and ranking models unloaded until an operation needs them.

## 5 Current State

The review examined source code. The review did not measure startup time.

| Component or method | Current behavior |
|---|---|
| `InitializePersistenceAsync` | Waits for storage setup, migrations, audit, and retention work. |
| `RedactionAudit.RunAsync` | Reads all event rows for a session before it applies `MaxEventsPerSession`. It also lists and reads artifacts. |
| `ApplicationComposition.CreateAsync` | Prepares memory, scratchpad, and skill catalogs before the shell starts. |
| `BindRepositoryAsync` | Reads prompts and configuration. It repeats service binding and skill refresh during the initial repository open. |
| `ModelComposition.CreateAsync` | Reads catalogs. It can request credentials or provider capabilities before the shell starts. |
| `IntegrationComposition` | Connects configured MCP profiles before the shell starts. One startup path connects profiles in sequence. |

The same-repository check mainly avoids another scratchpad binding. Most other binding work can repeat.

Embedding and ranking models already load when needed. Repository extension autoload already remains disabled.

## 6 Proposed Design

### 6.1 Measure the work

1. Add measurements to the existing startup diagnostics.
2. Measure each startup component separately.
3. Record the time to the first display.
4. Record the time to the first model request.
5. Record the time to the first MCP call.

Measure these operations:

- Configuration and prompt reads.
- Storage setup, audit, and retention.
- Provider catalog reads.
- MCP connections.
- Skill discovery.
- Repository binding.

Use these conditions:

- Small and large event histories.
- Small and large sets of skill files.
- Slow optional connections.
- No network connection.

Keep inputs controlled between comparisons. Report work that moves from startup to the first request.

### 6.2 Limit storage reads

1. Add bounded event reads to the existing event store.
2. Apply the sample limit in the database query.
3. Read rows in stable sequence order.
4. Read session lists in pages where necessary.
5. Read artifact lists in pages where necessary.

Set a maximum size for each page. Use a stable cursor to select the next page.

Do not load a complete list before dividing the list into pages.

### 6.3 Reuse completed audit work

Add checkpoints through the existing storage component.

Record these facts:

- Sanitizer version.
- Audit options.
- Event range.
- Artifact content identity.

Do not store secret values in a checkpoint.

1. Check one bounded batch.
2. Complete required artifact repairs.
3. Save the checkpoint only after the batch succeeds.

If a batch stops before completion, resume the incomplete work after restart. If sanitizer rules or audited content change, mark the affected checkpoint invalid.

Keep sampled events separate from complete audit coverage. Do not mark unchecked events as audited.

Let later bounded passes include new events.

Keep the existing artifact repair procedure. Save successful repair evidence only after both content storage and required deletion succeed.

If a checkpoint is missing or corrupt, use the existing audit procedure.

Do not expose unchecked content to reduce startup time. Keep current audit work before startup completion until content readers provide equivalent protection.

If readers need extra checks, extend the existing readers. Do not add another sanitizer.

### 6.4 Separate required retention work

1. Identify retention work that enforces a storage limit.
2. Keep that work before writes that depend on the limit.
3. Identify optional cleanup.
4. Move only optional cleanup after the first display.

Use the existing retention service. Limit each database transaction.

If cleanup stops, keep enough state to resume the required work.

### 6.5 Avoid repeated repository setup

Keep an immutable binding record in `RepositoryScopedBindingCoordinator`.

Record these facts:

- Repository identity.
- Policy generation.
- Configuration inputs.
- Prompt inputs.
- Catalog inputs.
- File membership for catalogs that depend on directory contents.

At the initial repository open, check the record against current inputs and permissions.

If the record remains valid, reuse the prepared state. Keep required command results, warnings, and lifecycle events.

Repository path equality alone is insufficient proof.

If a prompt, skill, configuration, or policy changes, use the existing binding procedure. If the record is invalid, use that procedure.

Prepare all candidate state before making it available. Keep the current gate, idle check, and rollback procedure.

Do not expose only part of a new binding.

Keep the `StartupAuthorityFingerprint` check. If specialized integration permissions change, keep the existing restart requirement.

Give each service one setup owner. Eliminate a repeated refresh only when the binding record proves that its result remains valid.

### 6.6 Delay optional provider work

Keep required configuration, policy, core storage, and hooks before the first display.

1. Use valid local provider catalogs when available.
2. Delay credential requests for unrelated providers.
3. Delay optional capability refresh through the existing provider lifecycle.

If the selected model needs missing capability data, wait for that data. If the operation cannot complete, return the existing unavailable result.

Keep catalog checks and credential refresh rules. Do not infer capabilities from an invalid or expired catalog.

### 6.7 Delay optional MCP connections

Separate adapter creation from connection completion.

1. Create the MCP adapter through the existing composition code.
2. Start approved automatic connections through the existing MCP manager.
3. Track the connection tasks through the host lifetime.
4. Limit the number of simultaneous connections.

Run connections simultaneously only when the profiles are independent.

Keep each profile's required order:

1. Complete the connection.
2. Complete required policy work and hooks.
3. Make permitted tools available at the existing catalog boundary.

Keep current capability publication rules where they impose additional ordering.

Trace both startup connections and later `AutoConnectAsync` calls. Give each profile one connection owner.

If manual connect or first use finds a pending connection, use that same operation where possible. Do not start a duplicate connection.

Permit the first display and independent local operations while optional connections remain pending.

Keep the tool catalog stable during an active model request. Make new tools available only at the existing permitted boundary.

Use the same dependency checks without an interactive interface. A dependent request must wait within existing deadlines and cancellation rules.

Do not fail a request solely because startup delayed its required connection.

### 6.8 Keep control of background work

Track every task that continues after startup.

During shutdown, request cancellation of those tasks. Wait for task completion under the existing shutdown rules.

After a repository change, reject results that belong to the previous binding.

Do not discard a task reference while the task can still report a result or failure.

## 7 Public Contracts

Keep current commands and permission decisions. Use existing MCP status and tool availability records where possible.

Use host-owned records for checkpoints and binding data. Keep provider, terminal, and extension types out of persistent state.

If the design needs a new readiness state, define its owner and transitions in Core. Keep readiness decisions in the host.

## 8 Project/File Changes

Inspect these files before changes:

- `src/Threadsmith.App/Program.cs`
- `src/Threadsmith.App/HostFoundation.cs`
- `src/Threadsmith.App/ModelComposition.cs`
- `src/Threadsmith.App/IntegrationComposition.cs`
- `src/Threadsmith.App/ApplicationComposition.cs`
- `src/Threadsmith.Persistence/RedactionAudit.cs`
- `src/Threadsmith.Persistence/SqliteEventStore.cs`
- `src/Threadsmith.Persistence/RetentionService.cs`

Trace artifact storage, catalog refresh, MCP management, and shutdown from these files.

Change storage contracts or migrations only where necessary. Use existing persistence, MCP lifecycle, provider, skill, and architecture test projects.

## 9 Ordered Tasks

1. Compare current source with the source revision in section 2.
2. Exclude work that the current source already completes.
3. Record required and optional startup dependencies.
4. Record the component that controls each task during shutdown.
5. Measure startup before code changes.
6. Implement the bounded queries from section 6.2.
7. Implement the audit checkpoints from section 6.3.
8. Separate retention work as specified in section 6.4.
9. Implement the binding record from section 6.5.
10. Delay optional provider work as specified in section 6.6.
11. Delay optional MCP connections as specified in section 6.7.
12. Check task lifetime against section 6.8.
13. Test each change separately.
14. Measure startup and the first dependent request.
15. Record results and remaining work in this plan.

## 10 Testing

These tests apply to implementation. The plan-writing task did not run builds or tests.

| Area | Required test conditions |
|---|---|
| Audit queries | Use a large history. Exceed one page. Add new events between passes. |
| Audit checkpoints | Change sanitizer rules. Corrupt a checkpoint. Interrupt a batch. Repeat an artifact identity. |
| Artifact repair | Fail content storage. Fail deletion. Check that failed repair produces no success checkpoint. |
| Retention | Exceed a limit. Write during cleanup. Interrupt cleanup. Restart. Keep referenced data. |
| Initial binding | Open without changes. Change a prompt. Add or delete a skill. Change user configuration. |
| Binding recovery | Revoke trust. Change startup permissions. Switch repositories. Fail rollback. |
| Provider catalogs | Use valid, expired, and absent catalogs. Select one provider while another remains unused. |
| Provider requests | Use expired credentials. Exceed a timeout. Cancel a request. Start without a network connection. |
| MCP connections | Delay or fail a profile. Exceed the concurrency limit. Invoke a required hook. |
| MCP requests | Connect manually during automatic connection. Request a tool before connection completion. |
| MCP lifetime | Complete a connection after a repository switch. Stop the host with pending connections. |

Use fake transports and controlled test barriers to check operation order. Count query rows, refresh operations, and connections.

Check when new state becomes available. Do not use network delays as proof of operation order.

Keep these acceptance scenarios valid:

- Scenario C3.
- Scenario J.
- Scenario M.
- Scenario Q.
- Scenario R.
- Scenario Y.

Use MTP-235 through MTP-239 for MCP procedures. Use MTP-243 through MTP-245 for skill and model procedures.

Before implementation completion, run applicable checks from `CONTRIBUTING.md` and current CI workflows.

Record exact commands and results. Report Windows, Linux, and macOS results separately.

## 11 Security/Permissions

Keep sanitization at existing content access points. A checkpoint does not replace those checks.

Do not log stored content or credentials during measurements.

Check current permissions before reusing a binding. Use normal secret resolution for approved MCP profiles.

Keep all required policy hooks. Pass cancellation tokens through new asynchronous operations.

Apply resource limits to the total foreground and background work.

## 12 Observability

Use existing logs and operation status.

Report these facts:

- Event rows and artifacts read.
- Audit batches completed.
- Binding steps reused.
- Connection state.
- Sanitized failures.

Keep pending, ready, failed, and cancelled states distinct. Use only predefined labels.

Keep failure details available after the first display.

## 13 Migration/Compatibility

Give persistent checkpoints a format version through the existing migration component.

If a checkpoint is absent or incompatible, treat its data as unaudited.

Keep binding records in memory unless a specific requirement needs persistent storage. Do not store live services or credentials in a binding record.

Keep configured automatic connections. Change only when those connections complete relative to the first display.

Keep required waits for dependent requests in both startup modes.

## 14 Acceptance Criteria

- The database applies event limits before rows enter application memory.
- Each page uses bounded memory and permits progress to the next page.
- An interrupted audit resumes without marking unchecked data complete.
- Failed repair cannot produce a successful repair checkpoint.
- Valid initial binding reuse avoids repeated catalog discovery and prompt parsing.
- Changed inputs cause normal binding or the existing restart requirement.
- Optional network work does not delay the first display or independent local operations.
- Dependent requests wait for valid readiness.
- Tools remain unavailable until required policy work completes.
- Each MCP profile has one connection owner.
- Shutdown includes every tracked background task.
- Repeated measurements demonstrate the claimed improvement.

Set acceptable performance limits before comparison. Report the first-use delay and any remaining startup waits.

## 15 Risks

Changed sanitizer rules can make checkpoints invalid. Include the rule version in each checkpoint.

A binding record can miss a new file. Include directory membership in the relevant checks.

An earlier display can expose incomplete services. Keep host checks before dependent requests.

Simultaneous connections can increase load or change hook order. Keep resource limits and required operation order.

## 16 Documentation

Keep this plan in the planning index. Record implementation results here.

Keep completed milestone contracts unchanged. Document visible pending connections for operators.

Change manual procedures only when the operator workflow changes. Do not describe sampled audit data as complete coverage.

Writing reference: [asd-ste100 skill](https://github.com/danyuchn/asd-ste100-skill/blob/master/SKILL.md).
The rewrite uses the skill's structural rules. The rewrite does not establish compliance with the official dictionary.

## 17 Open Decisions

| Decision | Required action until resolved |
|---|---|
| Which audit work can complete after startup? | Keep the work before startup completion until content protection remains equivalent. |
| Which retention work enforces a hard limit? | Keep that work before dependent writes. |
| Which inputs prove a valid initial binding? | Use the full binding procedure when proof is incomplete. |
| Which profiles and hooks can run simultaneously? | Keep dependent operations in their current order. |
| Which provider work can wait until later? | Keep required waits for the selected model. |
| Which performance limits apply? | Set limits before the comparison. |
