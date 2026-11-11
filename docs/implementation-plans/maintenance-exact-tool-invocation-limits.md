# Limits for repeated tool invocations

**Status:** Planned
**Delivery track:** Maintenance
**Prerequisites:** The shared `ToolCallHistory`, tool runtime configuration, conversation correction paths, and deployed prompt catalog must remain available.

## 1 Objective

Permit a configured number of exact tool invocations in one turn.
Set the default for `run_process` to `3`.
Let the user configure this number for any registered tool.
Reject the next invocation with a shared message that gives the count.

Use short sentences and direct instructions in this document.
Treat code names and repository terms as technical names.
Use [ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf) as the language reference.

## 2 Architectural Context

Use the existing tool execution path.
Extend `ToolCallHistory` for admission and counts.
Use `ToolRuntimePolicy` for configured definitions.
Use the existing correction paths for rejected requests.

The parent conversation, child agents, and model-based skills already use the shared history type.
Apply the same rule in these three paths.
Keep tool execution inside the existing policy, approval, cancellation, event, and budget boundaries.

Follow these contracts:

- [Planning governance](planning-governance.md).
- [Shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).
- [Portable C# guardrails](../guardrails/portable-csharp-guardrails.md).
- [ADR-41](../architecture/adr-41-canonical-cache-optimized-model-requests.md).
- [Prompt operations](../operations/prompts.md).
- [Resource limits](../operations/resource-limits.md).
- [Conversation correction behavior](../operations/conversation-loop.md).

## 3 Scope

- Add `NumberOfExactToolInvocationsPerTurn` to `ToolDefinition`.
- Add a configured override for every registered tool ID.
- Add the setting to the existing `tools.runProcess` section.
- Count accepted invocations only when the effective value is positive.
- Return the count in a shared rejection message.
- Keep identical sibling requests invalid within one model response.
- Update tests, prompt contracts, configuration examples, and maintained documentation.

## 4 Non-Scope

Do not add automatic retries or a process-specific execution path.
Do not add a result cache or a new loop detector.
Do not change tool arguments to make a repeated command different.
Do not change tool permissions, process authority, or automatic validation behavior.
Do not change the meaning of `ToolIdempotency`.
Do not stage, commit, push, or open a pull request without a user instruction.

## 5 Current State

`ToolCallHistory` stores the tool ID and the tracked argument string in a set.
The tool ID comparison ignores case.
The argument comparison uses the existing caller normalization.
For tools with concept hints, the history removes those hints before comparison.

`AllowDuplicateInvocations` permits repeats in later responses.
It does not permit identical siblings in one response.
`run_process` does not enable this property.

The callers record accepted requests before execution.
Thus, an execution failure can prevent an identical retry.
A rejected batch does not enter the accepted history.

The existing duplicate message is a Markdown asset:
`src/Threadsmith.Execution/Prompts/Correction-Tool-DuplicateInvocation.md`.
Its current token contract contains only `ToolName`.

## 6 Proposed Design

### 6.1 Terms

| Term | Meaning |
|---|---|
| Turn | One conversation execution, including its model continuations. |
| Exact call | The same canonical tool ID and tracked argument string. |
| Accepted invocation | A request recorded after successful admission of the complete batch. |
| Count | The number of accepted exact invocations in the current turn. |
| Limit | The maximum total accepted exact invocations, including the first invocation. |

A child execution and a skill procedure keep their existing independent histories.
They do not use the parent counter.
A context summary or evidence projection must not reset a history.
A new execution starts with an empty history.
Preserve the existing treatment of steering within an active execution.

### 6.2 Definition values

Add an `int` property named `NumberOfExactToolInvocationsPerTurn`.
Its default value is `0`.
Reject negative values.
Accept values from `0` through `Int32.MaxValue`.

| Value | Admission rule |
|---:|---|
| `0` | Accept the first exact call. Reject each repeat. Use the existing set without a counter. |
| `1` | Accept one exact call. Reject each repeat. Maintain a counter. |
| `3` | Accept three exact calls. Reject the fourth and each later repeat. |
| Positive `N` | Accept at most `N` exact calls. Reject the next repeat. |

Zero does not disable the limit.
It does not permit unlimited calls.
For a zero-value rejection, the message count is `1`.
Derive this value from set membership without a numeric counter.

Set the declared `run_process` value to `3`.
Use the same default in application configuration.

### 6.3 Configuration for any tool

Add `NumberOfExactToolInvocationsPerTurn` to entries in `tools.runtime.byTool`.
Use a nullable integer in the override contract.
An absent override retains the declared value.
An explicit zero overrides a positive declared value.

Use the existing list and exact registered IDs.
This list already supports IDs that contain colons.
Apply the override to built-in, MCP, and extension registrations.
Apply it when a dynamic tool registers or replaces an existing registration.

Add `NumberOfExactToolInvocationsPerTurn` to `tools.runProcess`.
Its default is `3`.
The per-tool runtime override takes precedence over this setting.
Preserve mandatory machine constraints after ordinary override resolution.
For a ceiling comparison, zero permits one exact call.
Do not add a second setting under `tools.config`.
Do not add a global repetition setting under `tools.runtime.defaults`.

Example:

```json
{
  "tools": {
    "runProcess": {
      "NumberOfExactToolInvocationsPerTurn": 3
    },
    "runtime": {
      "byTool": [
        {
          "toolId": "run_process",
          "NumberOfExactToolInvocationsPerTurn": 5
        },
        {
          "toolId": "list_files",
          "NumberOfExactToolInvocationsPerTurn": 2
        },
        {
          "toolId": "server:search",
          "NumberOfExactToolInvocationsPerTurn": 0
        }
      ]
    }
  }
}
```

This example permits five exact `run_process` calls in one turn.
It permits two exact `list_files` calls.
It rejects repeated exact `server:search` calls.
The tool must still be available and permitted.

Preserve the existing trusted sources for runtime overrides.
Declare the new `tools.runProcess` property in the configuration permission contract.
Do not let repository configuration increase this limit.
Use the existing configuration precedence and startup binding.
Require a restart for a changed value.

Validate configured values before tool execution.
Reject negative, fractional, Boolean, empty, null, and out-of-range values.
An omitted property is different from an invalid value.
Validate entries for tools that are not yet loaded.
Preserve validation for duplicate tool IDs.

### 6.4 History and counters

Keep the existing set for exact call identity.
Add numeric counts only for definitions with a positive effective value.
Do not create or increment a counter for a zero-value definition.

For a positive value, compare the count with the limit before increment.
Increment once after successful batch admission, before execution dispatch.
Do not increment for a rejected invocation.
Do not increment for each scheduling step, policy check, or event.
Do not overflow the counter at `Int32.MaxValue`.

Count an admitted request even if execution fails, times out, or returns a policy denial.
Count an admitted request if cancellation prevents its later dispatch.
Preserve the existing admission boundary.
The limit must not imply that a failed command had no effects.
Do not retry a command automatically.

Keep counts separate for each exact call.
A different tool ID or argument string uses a different entry.
Preserve existing concept-hint normalization and argument handling.
Do not introduce JSON equivalence rules in this change.

### 6.5 Batch admission

Copy both the set and positive counts into the tentative batch history.
Do not share mutable counter entries between the two histories.
Check the complete response before execution.

Reject identical siblings even when the remaining quota could admit them.
Keep the existing whole-batch rejection behavior.
Do not execute a sibling from a rejected batch.
Do not consume its quota.

Keep the accepted history unchanged when any batch check fails.
Record each admitted invocation once after successful batch admission.
Do not increment the accepted count during tentative validation.

Return the rejection reason and count from the shared admission check.
Use a small host-owned result if the current Boolean return is insufficient.
Do not calculate counts separately in each caller.

### 6.6 Shared messages

Extend `Correction-Tool-DuplicateInvocation.md` for an exhausted quota.
Use the same asset for every tool, including tools with a zero value.
Add the token `InvocationCount` beside `ToolName`.

Use this text:

> Tool '{{ToolName}}' already has {{InvocationCount}} accepted invocations with the same arguments. Try a different approach if you need more information.

For a limit of `3`, the fourth request reports `3` accepted invocations.
Further rejected requests still report `3`.
Do not count a rejected request as an execution.
Format the count with the invariant culture.
Do not put the command text or arguments in the message.

Keep the sibling rule distinct from quota exhaustion.
Use one shared Markdown asset for the sibling reason:
`Correction-Tool-DuplicateSiblingInvocation.md`.

Use this text:

> Tool '{{ToolName}}' occurs more than once with the same arguments in this response. Submit each exact request only once.

The sibling asset uses only `ToolName`.
Do not report zero completed invocations as a quota breach.
Do not add a message that applies only to `run_process`.

Update the complete filename and token catalog in `PromptContracts.cs`.
Use the existing immutable prompt loader.
Update both prompt reference documents in the same implementation change.
Update prompt tests and release checks for the new token and asset.

### 6.7 Shared callers and definition wrappers

Update the parent, child, and skill callers to use the shared admission result.
Render the quota message with the count from that result.
Use the existing bounded correction path.
Do not add a separate retry loop.

Preserve the configured value through `ConfiguredTool` and policy wrappers.
Preserve it when a source definition changes.
Resolve the effective value before the model request captures its tool registrations.
Keep the value host-owned.
Do not add it to model tool arguments or provider schemas.

## 7 Public Contracts

`ToolDefinition.NumberOfExactToolInvocationsPerTurn` is a nonnegative `Int32` value.
Its default is zero.
The `run_process` declaration defaults to three.

The per-tool override has the same property name.
Its absence retains the declared value.
The setting controls admission only.
It does not grant permission to execute a tool.

The quota prompt requires `ToolName` and `InvocationCount`.
The sibling prompt requires `ToolName`.
No durable event or database schema change is required.

## 8 Project/File Changes

| Area | Files or components |
|---|---|
| Tool definition and counts | `ToolContracts.cs`, `ToolCallHistory.cs`, `ToolRegistry.cs` |
| Configured definitions | `ToolRuntimeOptions.cs`, `ConfiguredTool` |
| Process defaults | `BuiltInTools.cs`, `HostFoundation.cs`, `ConfigurationBootstrap.cs` as required |
| Configuration authority | `PermissionConfiguration.cs`, `SecurityPolicyConfigurationLoader.cs` |
| Shared rejection | `CorrectiveMessageFactory.cs`, parent conversation, `ChildAgentModelLoop.cs`, `ModelSkillProcedureRunner.cs` |
| Prompt assets | The two shared assets in `Threadsmith.Execution/Prompts`, plus `PromptContracts.cs` |
| Existing repeat declarations | `ReadFileTool`, native validation definitions, `MemoriesTool`, `InvokeSkillTool`, `RepositoryArcheologyTool` |
| Tests | ModelTooling, Planning, ParallelAgents, Skills, Architecture, and prompt payload tests |
| Documentation | Configuration examples, resource limits, prompt references, Scenario C2, and MTP-253 |

## 9 Ordered Tasks

1. Inspect every `AllowDuplicateInvocations` declaration and consumer.
2. Add the numeric definition property and its validation.
3. Migrate the existing repeat-enabled declarations as specified in section 13.
4. Set the declared `run_process` default to three.
5. Add configuration binding through the existing runtime policy.
6. Preserve the configured value through all definition wrappers.
7. Extend the shared history with positive-value counters.
8. Add the shared rejection result and prompt token.
9. Update all three model execution paths.
10. Add deterministic tests at the real admission boundaries.
11. Update the maintained acceptance, manual, configuration, and prompt documents.
12. Run the final validation commands from `CONTRIBUTING.md`.
13. Review execution, ownership, batch rejection, and cancellation outside the diff.
14. Record results and limits in this plan.

## 10 Testing

Use guardrail G-24.
Use explicit completion signals and test-runner cancellation.
Do not use sleeps, elapsed-time assertions, or retries until tests pass.

Test these cases:

| Case | Expected result |
|---|---|
| Definition value omitted | The first exact call passes. The next call fails with count `1`. |
| Value `1` | One call passes. Its counter becomes `1`. |
| Process default `3` | Three calls pass across responses. The fourth fails with count `3`. |
| Another tool configured to `2` | Two exact calls pass. The third fails with count `2`. |
| Explicit zero override | A positive declaration becomes single-use. No numeric counter is stored. |
| Different exact arguments | Each argument string has an independent allowance. |
| Rejected repeat | The accepted count does not change. |
| Execution failure or cancellation after admission | The accepted attempt consumes one invocation. |
| Invalid or duplicate sibling batch | No sibling executes. No accepted count changes. |
| Rejected batch followed by a corrected batch | The corrected batch retains its full unused allowance. |
| Independent histories and copied histories | One history cannot change another history's counters. |
| Summary or context projection | The count remains unchanged. |
| New turn or independent child execution | The history starts empty. |
| Concept hints change | Existing operational identity rules remain effective. |
| Dynamic registration and replacement | The configured override remains effective. |
| Wrapper or captured-policy application | The numeric value remains effective. |
| Limit `Int32.MaxValue` | Comparison rejects the next call without integer overflow. |
| Invalid configured value | Configuration fails before tool execution. |
| Local prompt override with old tokens | The existing token validator reports the contract mismatch. |

Use a seeded history to test the integer boundary.
Do not execute billions of calls.
Use a recording process manager for process admission tests.
Verify the count, policy calls, activity, and model-visible rejection.
Verify the shared message in parent, child, and skill paths.
Verify that the zero-value path has no numeric counter entries.

Run the focused projects and the full product validation:

```powershell
dotnet restore src/Threadsmith.sln
pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
git diff --check
```

Use the platform-specific ripgrep environment setup from `CONTRIBUTING.md`.
Run focused whitespace and informational analyzer checks for changed C# files.
Record exact commands, failures, skips, and unavailable platform checks.
Do not present a Windows result as Linux or macOS validation.

## 11 Security/Permissions

Use trusted configuration for the new process setting and runtime overrides.
Apply the existing source validation before configuration binding.
Reject invalid values rather than clamp them.

Every admitted repeat must pass the normal tool pipeline.
Keep command preparation, approval scope, and process policy checks for each invocation.
An earlier approval does not create new authority.
Existing approval grants retain their defined scope.

Do not include arguments or command output in the rejection message.
Do not let a prompt edit change the numeric admission rule.
Keep counters in host-owned turn state.

## 12 Observability

Use the existing correction event and tool-result messages for rejected requests.
Keep real tool activity and completion events for admitted executions.
Do not create process-start activity for a request that admission rejects.
Do not count a rejected request as an executed tool in statistics.
Keep headless and interactive outcomes equivalent.

## 13 Migration/Compatibility

Replace `AllowDuplicateInvocations` with the numeric contract.
Remove the old Boolean after all declarations, callers, tests, and documentation migrate.
Do not keep two controls with conflicting precedence.

Use zero for definitions that currently reject repeats.
Use `Int32.MaxValue` for definitions that currently set the Boolean to true.
This value preserves their practical repeat allowance within the new integer contract.
It is a finite maximum, not an unlimited sentinel.
An explicit user override can reduce or replace that value.

The affected declarations include file reads, native validation, memories, skill invocation, and repository archeology.
Preserve skill invocation deduplication inside its existing lifecycle service.
A permitted tool repeat does not authorize a duplicate skill workflow.

No database migration is required.
Do not change durable invocation keys or replay recorded process effects.
Update custom copies of the changed prompt to include its required count token.

## 14 Acceptance Criteria

1. The `run_process` default permits three exact invocations per turn.
2. Its fourth exact invocation returns the shared message with count `3`.
3. A user can configure the same property for any registered tool.
4. Zero retains single-use behavior without a numeric counter.
5. Positive values include the first invocation in the maximum.
6. Rejected requests and rejected batches do not consume quota.
7. Identical siblings remain invalid in every model execution path.
8. Parent, child, and skill paths use one admission implementation.
9. Every admitted invocation retains normal policy and execution visibility.
10. All new model-facing wording resides in cataloged Markdown assets.
11. The prompt references describe the exact filenames and tokens.
12. Existing repeat-enabled tools retain their declared compatibility limits.

Update [Scenario C2](acceptance-scenarios.md#scenario-c2--conversation-native-corrective-turns) during implementation.
Update [MTP-253](manual-test-plan.md#mtp-253--conversation-native-corrective-turns-and-mcp-tool-name-aliasing) during implementation.
Add checks for configured limits, process reruns, counts, and sibling rejection.
Do not mark those procedures as executed when only automated tests run.

## 15 Risks

An incorrect interpretation of zero can disable duplicate protection.
Use the value table in section 6.2 as the contract.

A shared mutable counter can change the accepted history during batch validation.
Copy values and test rejection without state changes.

A wrapper can discard the configured value when a tool definition changes.
Test the existing wrapper refresh path.

Removing the Boolean can change existing repeat-enabled tools.
Inventory every declaration before migration.

A shell command can produce effects before it fails.
Permit an explicit repeat without automatic retry.
The configured maximum does not prove that a repeat is safe.

## 16 Documentation

During implementation, update these maintained sources:

- `.threadsmith/config.example` for the process default and generic per-tool example.
- `.threadsmith/resource-limits.example` where it describes tool runtime limits.
- `docs/operations/resource-limits.md` for values, precedence, sources, and restart behavior.
- `docs/operations/conversation-loop.md` for admission and count semantics.
- `docs/operations/pr-fetch.md` for the replacement of the old Boolean contract.
- `docs/operations/prompts.md` for both prompt contracts.
- `docs/prompt-file-reference.md` for both prompt contracts.
- Scenario C2 and MTP-253 for externally observable behavior.

Keep completed milestone documents unchanged.
Keep the planning README entry limited to navigation.

## 17 Open Decisions

None.
The process default is three total exact invocations.
The first invocation consumes one place in a positive limit.
Zero preserves single-use behavior.
The existing runtime override list supplies configuration for any tool.
