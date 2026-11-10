# Plan 106 — Shared action policies and shell command control

**Status:** Planned. No implementation is part of this document change.
**Delivery track:** Maintenance — process authorization hardening.
**Prerequisites:** Read [planning governance](planning-governance.md), [shared context](00-shared-context.md), and the root `AGENTS.md`. Before C# changes, read the [C# guardrails](../guardrails/portable-csharp-guardrails.md). Use the current tool pipeline, source-aware configuration loader, session policy binding, and settings coordinator.

This revision replaces the earlier proposal in this file. Do not implement its former deny-list mode or repository policy sources.

**Writing rules:** Use ASD-STE100 Simplified Technical English for implementation instructions. Use short sentences, active voice, and one instruction per step. Treat code names and domain terms as technical names. This document does not claim certified language compliance.

## 1 Objective

Control each action requested through `run_process`. Deny an action unless an explicit rule permits execution or permits user approval.

Put reusable policy logic in a new project, `Threadsmith.Policies`. Keep one policy evaluation path. Keep one policy configuration update path.

Use separate settings for Windows, macOS, and Linux. Within each platform, use separate shell profiles. Accept these settings only from machine and user configuration.

Prevent direct shell workarounds for native-tool restrictions. Allow normal Git and .NET workflows through explicit shipped rules. Do not claim OS isolation or control of all descendant process behavior.

Keep policy lists inside the host. Do not publish rules or grants to the model. Section 12 defines the visibility boundary.

### Technical terms

| Term | Meaning |
|---|---|
| Action | One operation identified by a host adapter. |
| Fact | A typed value supplied by the host about an action. |
| Matcher | A host-owned test of action facts. |
| Rule | Matchers, an outcome, an identifier, and optional override references. |
| Grant | A recorded user approval with a defined scope. |
| Snapshot | An immutable, validated set of effective rules and grants. |
| Binding | The current session authority identity and generation. |
| Fail closed | Deny execution when authorization cannot be established. |

## 2 Architectural Context

Follow [ADR-08](../architecture/adr-08-policy-gated-side-effects.md) and [ADR-11](../architecture/adr-11-central-tool-policy-pipeline.md). The host owns permission. The model cannot approve its requests.

Reuse the existing tool invocation pipeline. Reuse `RunProcessTool`, `IProcessManager`, process tracking, cancellation, hooks, budgets, and activity events.

Use the scoped approval flow in [ADR-54](../architecture/adr-54-direct-fetch-approval-durations.md) as a lifecycle precedent. Do not change web-fetch grant semantics in this work.

The new project has a concrete purpose. It owns reusable matching, rule resolution, grant rules, and configuration transformations. It supports independent tests. It prevents copies of these rules across consumers.

Do not create a second tool policy engine. `DefaultPolicyEngine` remains the tool admission owner. It calls the shared evaluator for action decisions.

## 3 Scope

- Add generic policy contracts, matchers, evaluation, grants, and update logic in `Threadsmith.Policies`.
- Add a command domain within that project. Keep shell parser adapters at the existing tool boundary.
- Add machine/user policy sources, platform profiles, and shell profiles.
- Add shipped `Allow` rules for normal Git and .NET workflows. Deny unmatched actions.
- Keep rule catalogs, grants, and policy management details out of model-facing content.
- Add the five outcomes in section 6.1.
- Extend the existing approval boundary with permitted approval choices.
- Save persistent command approvals in user configuration through one shared service.
- Support parent agents, child agents, interactive use, and headless use through the same path.
- Add policy inspection, tests, operational documents, and migration diagnostics.

## 4 Non-Scope

- Do not add a sandbox, container runtime, firewall, or OS execution broker.
- Do not prove that an approved program cannot access HTTP or start another program.
- Do not automatically move native build, Git, web, MCP, hook, or script tools to this policy system.
- Do not change unrelated permission lists or their domain semantics.
- Do not add external matcher plugins or executable configuration.
- Do not add a new shell tool, process manager, approval queue, or configuration file writer.
- Do not enable a blocked tool or retry a denied action through another tool.

Generic design permits future consumers. It does not authorize a solution-wide policy migration in this work.

## 5 Current State

The following map comes from source inspection. Recheck declarations before implementation. Paths identify existing code, not completed changes.

| Source | Current responsibility and integration need |
|---|---|
| `src/Threadsmith.Tools/BuiltInTools.cs`, `RunProcessTool` | Passes command text to the configured shell. Declares only that shell as its executable. Add prepared action analysis. |
| `src/Threadsmith.Tools/ToolPolicy.cs`, `DefaultPolicyEngine` | Applies trust, executable, resource, and tool checks. Add action evaluation here. |
| `src/Threadsmith.Tools/ToolInvocationPipeline.cs` | Owns approval, hooks, budgets, activity, and execution admission. Preserve this lifecycle. |
| `src/Threadsmith.Tools/ProcessManager.cs` | Resolves the outer executable from absolute PATH entries and launches tracked processes. It does not bind commands inside a shell. Reuse it. |
| `src/Threadsmith.Core/ApplicationContracts.cs`, `IApprovalPolicy` | Uses a Boolean approval response. It cannot express the required choice sets. |
| `src/Threadsmith.Tools/WebFetchAuthorization.cs` | Has scoped approval coordination and stale-response checks. Reuse the pattern, not URL grant logic. |
| `src/Threadsmith.Interaction/Coordination/DirectFetchApprovalInteraction.cs` | Projects approval choices through frontend-neutral selection. Use this integration pattern. |
| `src/Threadsmith.App/SecurityPolicyConfigurationLoader.cs` | Resolves authority sources and machine enforcement. Add explicit machine/user source admission. |
| `src/Threadsmith.Core/ConfigurationPermissionAttribute.cs` | Declares source permissions. Its defaults include sources excluded from this feature. |
| `src/Threadsmith.Core/EffectiveSessionPolicy.cs` | Owns immutable authority and stale-generation rejection. Bind the command snapshot here. |
| `src/Threadsmith.Core/RepositorySettingsCoordinator.cs` | Owns serialized settings publication, change checks, and safe replacement. Keep this writer. |
| `src/Threadsmith.Tools/UserAllowedNetworkHostStore.cs` | Shows a focused list-update adapter over the coordinator. Do not copy its domain authority. |
| `src/Threadsmith.Tools/AgentToolPolicy.cs` | Narrows parent authority for children. Preserve that restriction. |
| `src/Threadsmith.App/HostFoundation.cs` | Selects the shell, composes process tools, and resolves executable defaults. Keep display and enforcement consistent. |

Current executable defaults include the platform shell, `git`, and `dotnet`. These are outer executable permissions. They are not command rules.

The general tool approval path has no Once, Session, or Always grant contract. Production composition uses `DenyApprovalPolicy`. A richer prompt must be connected to actual execution.

## 6 Proposed Design

### 6.1 Policy outcomes

Use these exact names in the configuration contract.

| Outcome | Execution rule | Permitted prompt choices |
|---|---|---|
| `Allow` | Execute if all other host checks pass. | No prompt from this rule. |
| `AlwaysDeny` | Reject. Never accept approval for this rule. | None. |
| `ApprovalAlwaysRequired` | Request approval for each invocation. Ignore remembered grants. | Approve Once; Deny. |
| `ApprovalRequiredSession` | Accept an applicable live-session grant. Otherwise request approval. | Approve Once; Approve for This Session; Deny. |
| `ApprovalRequired` | Accept an applicable session or persistent grant. Otherwise request approval. | Approve Once; Approve for This Session; Approve Always Going Forward; Deny. |
| No matching rule | Reject. Do not offer approval. | None. |

The host supplies the permitted choice set. The frontend must not add choices.

A persistent grant satisfies only `ApprovalRequired`. It does not override a rule or authorize an unmatched action.

A session grant satisfies `ApprovalRequiredSession` or `ApprovalRequired`. It cannot satisfy `ApprovalAlwaysRequired`.

Other mandatory host checks remain in force. Combine approval requirements into one request where possible. Do not display duplicate approval prompts.

### 6.2 New project and dependency direction

Create `src/Threadsmith.Policies/Threadsmith.Policies.csproj`. Target the shared .NET baseline. Add `tests/Threadsmith.Policies.Tests` to the solution.

Use these cohesive areas inside the new project:

| Area | Responsibility |
|---|---|
| `Contracts` | Rules, outcomes, action facts, decisions, approval choices, and grant identities. |
| `Matching` | Matcher catalog, validation, composition, and bounded evaluation. |
| `Evaluation` | Rule resolution, enforced constraints, grant checks, and explanations. |
| `Configuration` | Document schema, source layering, transformations, and update orchestration. |
| `Grants` | Session ownership, one-use consumption, revocation, and persistent approval records. |
| `Commands` | Command facts, executable identity, argument coverage, and command matchers. |

Keep the generic areas independent of shells, tools, frontends, and model providers. Do not use arbitrary property dictionaries as the public fact contract. Use typed facts with explicit domain identifiers.

The project must reference no other `Threadsmith.*` project. Do not put shell parser packages in this project.

Permit `Threadsmith.Core` to reference this leaf project for immutable policy and approval contracts. This is a deliberate change to its current no-project-reference rule. Update the architecture graph and shared architecture text together. Do not permit a reverse reference.

Only actual consumers may add references. Expected consumers are Core, Tools, App, and Interaction. Concrete frontends use the existing interaction projections. Do not add references to unrelated projects.

Define storage and publication ports in `Threadsmith.Policies`. Implement thin host adapters where Core services and configuration paths are available. The ports prevent a dependency cycle.

### 6.3 Configuration structure and source authority

Keep the feature under `tools.runProcess.commandPolicy`. Use the repository configuration serialization format. Use JSON examples in documentation.

The structure contains:

- `schemaVersion`.
- `platforms.windows`, `platforms.macos`, and `platforms.linux`.
- Common rules within each platform.
- Shell profiles within each platform.
- Stable rule IDs, matchers, outcomes, and explicit overrides.
- A user `alwaysAllow` collection for exact persistent approvals.

Select the actual host platform and the actual launch shell. Do not accept platform or shell identity from model arguments.

Normalize known shell identities. Distinguish Windows PowerShell from PowerShell Core where their behavior differs. Do not treat Bash, sh, and zsh as one grammar.

Combine platform-common rules with the selected shell rules. Resolve overlap with section 6.5. An absent or unknown platform/shell profile denies execution.

Declare source permissions as `Machine | User`. Exclude repository configuration, session files, user-owned per-repository permissions, environment variables, and CLI overrides.

Load shipped defaults as host-owned baseline rules. Apply machine rules, then user rules, by stable ID. Replace a same-ID rule as a complete record. Never merge matcher arrays by index.

Use explicit `disabledRuleIds` to remove inherited ordinary rules. An empty added-rule list adds nothing. It does not erase inherited rules.

Machine `enforced` constraints are separate. User edits cannot remove, replace, disable, or override them. Matching enforced requirements constrain the ordinary decision. They never supply permission for an otherwise unmatched action.

A missing enforced match is neutral unless a machine constraint explicitly requires membership in a closed rule set. An empty closed set permits no actions.

Validate the complete effective document. Reject unknown versions, matcher types, outcome names, duplicate IDs, invalid overrides, and malformed values. Do not keep using older permissive authority after invalid configuration is detected.

### 6.4 Matchers and composition

A rule can use any supported combination of matchers. All top-level matchers use AND semantics. Use an explicit `anyOf` group for alternatives.

Use a closed, host-owned matcher catalog. Each type has a stable discriminator, a schema, validation, and an evaluator. Configuration must not select CLR types or load assemblies.

Start with these matchers:

| Matcher | Required behavior |
|---|---|
| Exact value | Match one typed fact against one exact value. |
| Value set | Match one typed fact against a finite set. |
| Executable | Match a resolved external executable or a known shell command identity. |
| Argument prefix | Match complete initial argument tokens. Never match a string prefix. |
| Argument schema | Define flags, value options, positional arguments, and their permitted counts. |
| Path | Validate a path against explicit host-resolved roots and path rules. |
| Glob | Match one fact value. Do not match the complete shell command. |
| Regex | Match one fact value with explicit case and full-value semantics. |
| `allOf` / `anyOf` | Combine validated matcher nodes. Reject empty groups. |

Keep command-specific matchers in the command domain. Keep exact values, sets, composition, and bounded text matching reusable.

Return Match, NoMatch, or Error. An Error denies the invocation. Do not treat it as NoMatch and select a broader Allow rule.

Reject unknown fact names and incompatible fact types. Validate every branch before evaluation, including unused `anyOf` branches.

Require complete argument coverage for permission-bearing command rules. Combine coverage only within a successful matcher branch. Never combine partial coverage from failed alternatives.

Unknown options, extra arguments, and duplicate options fail unless the schema explicitly permits them. Define option-value consumption and the `--` boundary. Do not let a prefix matcher imply unrestricted trailing arguments.

Constrain case rules by fact type and platform. Use shell-specific comparison for cmdlets. Preserve Unix executable path case.

Bound rule counts, nesting, argument counts, input length, and pattern evaluation. Prefer non-backtracking regex where supported. Reject unsupported patterns or use a bounded timeout. A timeout denies execution.

Do not execute a command to determine whether its matcher applies.

### 6.5 Rule resolution and explicit exceptions

Do not infer priority from matcher count, regex length, or file order.

Use explicit `overrides` references for exceptions. A narrower rule can replace the outcome of an explicitly named broader rule. Configuration authors own the intended relationship. Do not claim automatic proof of pattern containment.

For each action:

1. Validate the snapshot and action facts.
2. Find matching ordinary rules.
3. Apply override relationships between matching rules only.
4. Reject cycles and missing references during document validation.
5. If no rule remains, deny without prompting.
6. If remaining rules have different outcomes, deny with a conflict reason.
7. If remaining rules have one outcome, use that outcome.
8. Apply matching machine-enforced constraints.
9. Check eligible grants only after the final outcome is known.

An ordinary `AlwaysDeny` can be an explicit exception within a broad Allow rule. A more specific exception needs an explicit override. No override can relax machine enforcement.

Combine enforced constraints conservatively. Denial wins. Then use this approval order: `ApprovalAlwaysRequired`, `ApprovalRequiredSession`, `ApprovalRequired`, `Allow`.

Keep grants outside this rule precedence graph. A saved approval is not a higher-priority Allow rule.

### 6.6 Example rule shape

The example defines contract intent. Task T02 must finalize and test its serialized names before implementation consumers use them.

```json
{
  "schemaVersion": 1,
  "platforms": {
    "windows": {
      "shells": {
        "powershell": {
          "rules": [
            {
              "id": "git-status-short",
              "matchers": [
                { "type": "executable", "name": "git" },
                { "type": "argumentPrefix", "values": ["status"] },
                {
                  "type": "argumentSchema",
                  "afterPrefix": true,
                  "flags": ["--short", "--branch"],
                  "positionals": { "maximum": 0 },
                  "allowUnknownOptions": false
                }
              ],
              "policy": "Allow"
            }
          ],
          "alwaysAllow": []
        }
      }
    }
  }
}
```

This example shows matcher composition. It is not the shipped Git default. Section 6.10 defines the shipped permissions. Other platforms and shells need their own effective profiles.

### 6.7 Shell analysis and execution binding

Keep private shell adapters under `Threadsmith.Tools`. Return only host-owned command facts to the policy project.

Analyze each pipeline stage and command in a sequence. Analyze supported substitutions and nested invocations. Check redirections and environment assignments as action effects, not harmless argument text.

Reject unsupported effects. An Allow rule for a read command must not authorize an unrelated file redirection.

Reject dynamic targets, runtime alias/function definitions, evaluation commands, and uninspectable shell expressions. Unknown syntax must not fall through to approval.

Do not use regex as a shell parser. Evaluate maintained parser options and their release footprint. If a shell lacks reliable support, deny its unsupported syntax.

Use fixed known alias metadata where safe. Cover PowerShell module qualification and invocation operators. Do not ask an executing shell to discover aliases or functions.

Bind analysis to the actual shell executable, startup arguments, working directory, environment, and original command text. Prevent repository shell selection from choosing a weaker interpretation.

Use the existing executable resolver for launch identity. If a preparatory resolver is needed, share the resolver with `ProcessManager`. Do not create a second PATH algorithm. Resolve against the environment that the child will receive. Bind the outer shell to its resolved absolute path through the existing launcher. This host-owned binding must not grant the model permission to choose arbitrary executable paths.

The shared resolver alone does not bind shell execution. `ProcessManager` searches absolute PATH entries. For `cmd`, a repository-local `git.exe` can take precedence over the authorized PATH executable when the shell receives `git status`. This is a lookup mismatch. It does not require a file replacement race.

For each external command, the adapter must produce a shell-safe reference to the authorized absolute executable path. Use the parsed command position and the actual shell's quoting and invocation rules. Do not use text replacement or concatenate unescaped paths. Preserve arguments, operators, and supported shell effects. Keep execution in the existing shell and process lifecycle. Do not add a separate direct-process route.

Reject the entire request before launch if the adapter cannot bind every command without changing its analyzed meaning. Never authorize one path and then let a bare command name select another path. Approval cannot waive this check. Changing PATH or checking file existence alone is not sufficient.

Preserve external paths and command kind. Do not reduce a path-qualified executable or cmdlet to an ambiguous basename. For supported built-ins, aliases, and cmdlets, bind the analyzed command kind and fixed target. Reject forms that can resolve to another function, alias, module, or external command at execution.

Define the binding method for each supported OS/shell pair in T02. Cover working-directory lookup, empty and relative PATH entries, PATH order, PATHEXT, startup behavior, and shell command precedence. Reject state changes that can invalidate the prepared binding. A shell is not supported until its real-shell tests pass.

Prepare one immutable action set and launch representation. Retain the original command text for request identity. Bind the prepared shell text, resolved paths, arguments, environment, and shell startup options to the same admission. Pass this binding through policy, approval, and execution. Execute only that prepared representation. Revalidate authority before launch. Do not re-read mutable command input after approval.

Document the remaining executable replacement race separately. Path binding does not prove that file contents cannot change after authorization. Do not classify deterministic shell lookup mismatches as that residual race.

If any action is denied or unresolved, reject the entire invocation before process execution. Do not start earlier allowed commands first.

A supported nested shell must be analyzed with its actual dialect. Otherwise deny it. Do not infer permission for child commands from an interpreter Allow rule.

An allowed or approved build or program can still execute hidden code. State this limit in user documentation. Do not claim that source parsing controls its internals.

### 6.8 Approval and grant lifecycle

Extend the current tool approval boundary with a typed request and response. Include the action summary, matched rule IDs, binding, and permitted choices. Keep these details on host-owned paths. Do not serialize the approval request, rule details, or remembered scope into model-facing results.

Route prompts through existing frontend-neutral interaction and selection. Reuse serialized prompt coordination. The terminal only projects the permitted choices.

Deny when interaction is unavailable. Headless mode may use applicable existing grants. It must not choose an approval option automatically.

Bind a grant to the exact invocation context by default:

- Operation domain and policy rule identity.
- Actual OS and shell identity.
- Resolved executable identities, original command text identity, and prepared launch identity.
- Arguments, working directory, effective child environment, and relevant execution options.
- Effective rule fingerprint and applicable authority context.

Do not widen an approval to every use of an executable. Show the user exactly what a remembered approval covers.

For compound commands, identify every action in the request. Use the intersection of permitted choices for actions that need approval. Never include an Always choice if any applicable requirement excludes it.

Consume Once grants atomically at final execution admission. A grant cannot authorize concurrent duplicate invocations or an automatic retry.

Keep session grants in memory under the live session owner. Clear them on new, resume, clone, repository change, or authority invalidation. Do not restore them from checkpoints.

Eligible children can use a grant within the same live session. Their assignment restrictions still apply. Include the requesting child in the approval activity.

Each `ApprovalAlwaysRequired` invocation needs its own prompt. Do not merge concurrent requests into one reusable approval.

For Always, save an exact approval record in the selected user profile's `alwaysAllow` collection. Recheck current policy before using the record. Manual removal must revoke that persistent route after policy refresh.

Do not persist credentials or raw sensitive arguments. If an exact safe record cannot be stored, exclude Always from the prompt. Do not silently broaden the record or downgrade the user's selection.

Cancellation, stale responses, rejected choices, and persistence failure deny execution. Report failures through existing activity. Never claim that a failed save succeeded.

### 6.9 One configuration update path

Create one `PolicyConfigurationService` in the new project. It owns all changes to this feature's rules, disabled IDs, and persistent approvals.

Use typed requests for add, replace, remove, disable, grant, and revoke. Do not expose arbitrary JSON patches or model-selected paths.

All host-managed entry points must call this service. This includes approval saves, settings controls, future CLI management, reset, import, and migration. Add entry points only when required by this feature.

Use this sequence for each update:

1. Verify the caller's authority and select the machine or user destination.
2. Enter the existing coordinated settings update.
3. Read the current document under the coordinator's gate.
4. Check revision or expected content.
5. Apply the typed transformation.
6. Validate sources, schema, matchers, rules, and grants with the shared compiler.
7. Preserve unrelated configuration.
8. Publish through `RepositorySettingsCoordinator`.
9. Refresh effective authority through the existing serialized binding lifecycle.
10. Publish the new snapshot and invalidate stale admissions.
11. Return a receipt with disk and activation results.

The storage adapter owns host IO, paths, and authorized policy keys. The generic service owns policy transformations and validation. Do not write a second atomic-file algorithm.

Manual file edits cannot use an application update API. Route their reload through the same document validation and effective snapshot publication path. Remove stale grants when their rule fingerprints no longer match.

Handle disk success and activation failure separately. Block new process admissions if the new effective authority cannot be activated. Do not roll back unrelated settings or report full success.

Session approvals use the shared grant authority. They do not write configuration. Do not create independent session allow lists in the frontend or tool.

This contract covers all lists introduced by this feature. Keep existing network, mutation, and skill stores unchanged. A later migration must use this service through a domain adapter, not copied logic.

### 6.10 Shipped defaults

Provide profiles for supported shells on Windows, macOS, and Linux. List exact grammar support for PowerShell, pwsh, cmd, Bash, sh, and zsh.

Ship explicit `Allow` rules for standard Git and .NET CLI operations. Do not limit these rules to inspection commands. Use this required baseline:

| Command family | Required shipped behavior |
|---|---|
| Git | Allow normal operations. Include status, diff, log, branch, checkout, switch, add, commit, merge, rebase, clone, fetch, pull, and push. |
| .NET CLI | Allow normal operations. Include information queries, restore, build, test, run, publish, pack, and standard package, tool, and workload management. |
| Direct HTTP clients and shell HTTP operations | Use `AlwaysDeny`. Include curl, wget, known aliases, `Invoke-WebRequest`, and `Invoke-RestMethod`. |
| OS component installation commands | Use `AlwaysDeny` for tested platform-specific forms. Distinguish these commands from normal .NET package, tool, and workload operations. |
| Other actions with no matching rule | Deny without prompting. |

Network access alone is not a reason to deny a Git or .NET operation. Do not require `--no-restore` for builds or tests. Permit implicit restore. Do not add blanket denials for Git remote operations or .NET restore, package, tool, or workload operations.

Define the executable identities, argument coverage, and command forms for these rules in T02. A permission-bearing schema can explicitly permit additional arguments. Use that mechanism where a normal CLI workflow needs it. Do not accidentally restrict normal workflows to the example options in section 6.6.

Review specific escape mechanisms as separate cases. Include Git shell aliases, external subcommands, injected executable options, and arbitrary extension dispatch. Do not assume that a program named `git-something` is Git. Do not assume that an unknown .NET extension is a standard CLI operation. Do not turn these cases into blanket network, installation, or delegation denials. Record proposed exceptions, their triggers, and their workflow impact in T02. Obtain a decision if an exception would block a normal workflow in this baseline.

An `Allow` outcome removes only this command policy's approval requirement. It does not authorize a push, destructive operation, or other action that needs authorization under another host rule.

Builds, tests, and other allowed programs can make HTTP requests or start processes internally. Accept this limit for this feature. Do not describe the defaults as network isolation.

Use explicit `overrides` for exception rules that overlap broader permissions. Users can deliberately change ordinary shipped rules. Machine-enforced denials cannot be relaxed.

Do not advertise the defaults to the model. Return a concise denial without rule details or alternative command suggestions. Do not add a model-facing correction prompt that reveals policy or recommends a workaround.

## 7 Public Contracts

The following names describe proposed contracts. They are not claims that these types already exist.

| Contract | Required content |
|---|---|
| `PolicyOutcome` | The five configured outcomes. |
| `ActionFacts` | Typed, immutable, host-provided domain facts. |
| `PolicyRule` | Stable ID, matcher tree, outcome, override IDs, and source identity. |
| `PolicySnapshot` | Compiled rules, enforced constraints, revision, and fingerprint. |
| `PolicyDecision` | Result, matched rules, reason code, and permitted approval scopes. |
| `PolicyApprovalRequest` / `PolicyApprovalResponse` | Exact request identity, allowed choices, and selected choice. |
| `PolicyGrant` | Exact scope, lifetime, originating rule fingerprint, and revocation identity. |
| `IPolicyMatcher` | Validation and bounded matching. No execution or prompting. |
| `PolicyConfigurationService` | Single typed update path. |
| Storage/publication ports | Host-authorized document access and binding publication. |

Use immutable records for data. Keep behavior in services. Propagate cancellation through IO and approval calls.

Keep parser, terminal, provider, and configuration-provider objects out of public results and durable state. Use stable reason codes for unmatched, explicit denial, conflict, analysis error, stale policy, approval denial, and save failure.

Preserve the existing `run_process` model input schema. The model cannot select a policy, grant duration, or override. Keep the model-facing tool contract independent of configured rules. Keep detailed decisions on host-owned interfaces. Use a separate, minimal projection for model-facing results.

## 8 Project/File Changes

| Location | Planned change |
|---|---|
| `src/Threadsmith.Policies/` | New shared library from section 6.2. |
| `tests/Threadsmith.Policies.Tests/` | Isolated matcher, rule, grant, and transformation tests. |
| `src/Threadsmith.sln` | Add the library and its test project. |
| `src/Threadsmith.Core/Threadsmith.Core.csproj` | Add only the leaf policy reference. |
| Core permission metadata and effective policy | Declare machine/user ownership and bind the compiled snapshot. |
| Core approval contracts | Extend the existing approval boundary with typed scopes. Preserve unrelated callers. |
| Tools policy and invocation pipeline | Evaluate prepared actions and route typed approval through normal admission. |
| Tools process/shell code | Add focused analysis adapters and shared resolution. Keep the existing launcher. |
| `AgentToolPolicy.cs` | Carry the same command snapshot and preserve parent restrictions. |
| App loader, composition, and rebind | Load admitted sources and register the service, adapters, and lifecycle owners. |
| Interaction coordination | Show permitted approval choices and policy explanations only to the user. |
| Existing settings publication | Add a thin policy document adapter. Keep the shared coordinator. |
| Architecture tests and documentation | Declare the new leaf boundary and prevent reverse references. |
| Existing test projects | Add integration coverage at each changed boundary. |

Keep new policy logic in the new project. Do not put matchers in App or the frontend. Do not scatter direct configuration writes across approval handlers.

## 9 Ordered Tasks

Each task must have a focused diff and actual validation evidence. Do not mark a task complete from an edit receipt alone.

### T01 — Confirm integration and ownership

- [ ] Confirm the active checkout and record unrelated local changes.
- [ ] Recheck the section 5 map against current declarations and callers.
- [ ] Trace manual, model, child, and headless entry points to actual execution.
- [ ] Record each existing permission and approval check that must remain active.
- [ ] Inventory readers and writers of the affected keys. Identify one owner for each responsibility.

Exit: The integration map has no unresolved execution or update entry point.

### T02 — Fix the schema and shell support matrix

- [ ] Define the serialized schema, source rules, and matcher catalog.
- [ ] Define argument coverage, overrides, source layering, and closed-set constraints.
- [ ] Evaluate parser packages, license impact, deployment size, and supported grammar.
- [ ] Define each OS/shell adapter's executable-binding method and rejection cases from section 6.7. Specify original and prepared launch identities.
- [ ] Define real-shell shadowing fixtures and the evidence required to declare each OS/shell pair supported.
- [ ] Define exact rules for the required Git/.NET baseline in section 6.10 for every platform/shell pair.
- [ ] Record specific exception candidates and unsupported forms. Do not replace the baseline with inspection-only rules.
- [ ] Define separate user-facing and model-facing projections. Audit prompts, tool descriptions, results, events, logs, and context retrieval for policy disclosure.
- [ ] Add the architecture decision and the narrow Core-to-Policies dependency change.
- [ ] Record all approved deviations in this document before code depends on them.

Exit: Schema fixtures and the support matrix can produce deterministic expected decisions.

### T03 — Add the reusable library

- [ ] Create the new projects and targeted references.
- [ ] Add immutable contracts, matcher registration, and validation.
- [ ] Implement bounded matcher composition and complete argument coverage.
- [ ] Implement deterministic rule resolution and enforced constraints.
- [ ] Add unit tests before connecting execution.

Exit: The shared library works without Tools, App, a shell, or a frontend.

### T04 — Add authority loading and the shared update service

- [ ] Add explicit machine/user permission metadata.
- [ ] Compile rules through one validation path for startup and updates.
- [ ] Implement typed transformations and the coordinated storage adapter.
- [ ] Implement snapshot publication, stale rejection, and removal/revocation handling.
- [ ] Test concurrent updates and disk/activation failure separately.

Exit: Every new policy-list mutation uses the same service and writer.

### T05 — Add shell analysis and tool admission

- [ ] Add shell adapters for the agreed grammar.
- [ ] Prepare immutable action facts and bind actual launch identity.
- [ ] Pin each external command to its authorized absolute path with shell-safe syntax. Reject forms that cannot preserve the analyzed meaning.
- [ ] Carry the prepared launch through the existing launcher. Reject stale or changed launch bindings.
- [ ] Evaluate through `DefaultPolicyEngine` before approval and execution.
- [ ] Reject the whole request if any action fails admission.
- [ ] Preserve normal hooks, budgets, activity, cancellation, and process cleanup.

Exit: A fake process manager proves that denied requests never execute. Real-shell shadowing tests prove that each supported adapter executes the authorized target or rejects the request. Fake-process tests alone cannot establish shell support.

### T06 — Add scoped approval and grant storage

- [ ] Extend the existing approval contract and interaction routing.
- [ ] Enforce choice sets in the host, not only the displayed menu.
- [ ] Add atomic Once consumption and memory-only session grants.
- [ ] Route Always and revocation through `PolicyConfigurationService`.
- [ ] Connect child requests and headless denial behavior.
- [ ] Test lifecycle changes while prompts and requests are pending.

Exit: Each policy produces exactly the specified prompt and grant behavior.

### T07 — Add defaults and operator information

- [ ] Ship platform/shell profiles that allow the required Git/.NET workflows and deny tested HTTP and OS component installation commands.
- [ ] Add user-facing effective-rule, source, conflict, and grant inspection.
- [ ] Keep policy catalogs and grants out of model prompts, tool descriptions, results, and model-visible diagnostics.
- [ ] Return concise model-facing denials without matching rules, alternate commands, or workaround advice.
- [ ] Update migration guidance and maintained acceptance/manual cases.

Exit: Users can explain a decision and remove a remembered approval without editing unrelated settings.

### T08 — Validate and review the full path

- [ ] Run focused tests, architecture tests, and applicable source checks.
- [ ] Run the final build/test commands from `CONTRIBUTING.md` and current CI workflows.
- [ ] Run shell integration cases on Windows, macOS, and Linux.
- [ ] Perform adversarial review outside the diff where needed.
- [ ] Re-review fixes through real entry points.
- [ ] Record exact commands, outcomes, limitations, and remaining platform checks here.

Exit: Section 14 is satisfied. Do not claim unavailable platforms passed.

## 10 Testing

Use deterministic tests under G-24. Use barriers and completion signals for concurrency. Use controllable clocks only for deadline behavior.

### Required test groups

| Group | Required cases |
|---|---|
| Outcomes | All five outcomes; unmatched denial; no approval prompt for denied requests. |
| Choices | Exact menu for each outcome; forged, unknown, and disallowed responses rejected. |
| Matchers | Each matcher; mixed combinations; `anyOf`; empty groups; unknown types; invalid facts; bounded regex failures. |
| Arguments | Unknown/duplicate options; attached values; option order; quoting; empty values; `--`; trailing argument rejection; branch coverage. |
| Resolution | Explicit exceptions; order independence; cycles; missing IDs; conflicts; enforced denial; enforced approval ceilings. |
| Sources | Machine/user accepted; repository, session, per-repository user, environment, and CLI excluded. |
| Defaults | Normal Git operations, including clone/fetch/pull/push; normal .NET restore/build/test/run/publish/pack and package/tool/workload operations; implicit restore; no mandatory no-restore flags; empty profiles; missing shells; unmatched actions denied. |
| Visibility | No rule catalog, matched rule IDs, grants, or policy-derived alternatives in model prompts, tool descriptions, results, events, retrieved context, or diagnostics; user approval and management views retain required details. |
| Exceptions | Specific dispatch and executable-injection cases; declared overrides; no blanket denial of normal Git/.NET network or package workflows. |
| Shell syntax | All supported dialects; paths; suffixes; module-qualified commands; aliases; pipelines; sequences; substitutions; redirections. |
| Unresolved forms | Dynamic names; runtime functions; eval; encoded commands; scripts; unsupported nested shells; parser errors. |
| HTTP denial | curl/wget variants; PowerShell HTTP calls; module qualification; invocation operators; nested member access expressions. |
| Literal text | Command names in comments and literal arguments do not become executable actions. |
| Launch binding | Authorized absolute target; original and prepared command identity; shell/startup/environment changes; working-directory shadowing; empty/relative PATH entries; PATH order; PATHEXT and command-kind precedence; stale snapshot rejection. |
| Grants | Once cannot replay; session clear; Always save/revoke; rule change; cross-session persistent use; restricted child use. |
| Persistence | Deduplication; unrelated settings preserved; revision conflict; concurrent saves; link rejection; cancellation; failed save; activation failure. |
| Integration | Parent/child/headless parity; activity and hooks; policy rebind; disabled tool; trust denial; budget cancellation. |
| Ownership | No direct list writers outside the service; no duplicate approval or process path; no forbidden project references. |

Use fake processes for denial tests. Use synthetic hosts and secret canaries. Do not fetch a public page or install OS components to prove rejection.

Test component installation requests as denied inputs. Verify no launcher call occurs.

Use real shells only for supported execution and identity tests. Report unavailable shells. A missing test environment is not evidence of supported enforcement.

For each OS/shell pair, create two harmless fixture executables with distinct completion markers. Put the authorized executable in an absolute PATH directory. Put a same-name shadow executable in the working directory. Submit a bare command through the normal tool admission and launch path. Assert that only the authorized marker appears, or that admission rejects the request before either executable starts. The shadow marker must never appear.

Include a Windows `cmd` case for the command form `git status` with a repository-local `git.exe`. Use a synthetic working directory and controlled fixture paths. Do not replace an installed Git executable. Test permitted execution without a shadow as a positive control. Add cases for extension precedence, PATH changes, startup aliases/functions, and compound commands where the shell supports these forms.

Use observable process completion and test-runner cancellation. Do not use sleeps or runner-speed deadlines. Record the shell executable, version, startup arguments, fixture environment, and outcome. Do not declare a shell supported from parser tests, fake launches, or a different shell's results.

Measure parsing and matching on small and large bounded inputs. Keep performance measurements separate from functional pass/fail deadlines.

For implementation validation, use these baseline commands after required fixture setup:

```text
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
```

Read current CI commands before submission. Reuse completed analyzer evidence where coverage is sufficient. Run additional analyzers only for concrete coverage gaps.

## 11 Security/Permissions

AlwaysDeny is final within its effective rule scope. Approval cannot bypass it.

Keep trust, tool restrictions, assignment scope, executable ceilings, protected paths, hooks, and resource ceilings. A command Allow is not a general host permission.

Protect new authority keys from generic model-facing edits. User configuration paths are not OS security boundaries. Approved code can still use the account's ambient authority.

Do not exempt a model-triggered operation merely because its process origin is Host. Do not expand this feature into unrelated native-tool authorization.

Do not claim HTTP confinement for approved builds, tests, scripts, or binaries. Container and network enforcement remain future work.

## 12 Observability

Use existing tool and approval activity. Do not add silent background policy work or a parallel event stream.

Show the selected platform/shell, rule IDs, source, outcome, grant scope, revision, and reason code only in user-facing management, approval UI, and protected administrative diagnostics. Show supported grammar and unresolved limits there.

Do not include allow/deny lists, rule definitions, saved grants, matched rule IDs, or policy configuration in model prompts, tool descriptions, tool results, or child-agent context. Do not change model-facing tool contracts when configured rules change.

Return only a concise rejection when policy blocks a model request. Do not include matching logic, alternative commands, or bypass suggestions. The model can observe success or denial. Do not claim that this hides all information about enforcement.

Keep detailed policy activity out of event projections and diagnostic retrieval that feed model context. Apply this restriction to parent agents and child agents. A hidden UI field alone is not sufficient.

Log once at the owning boundary. Do not add raw command text, URL queries, secrets, or full configuration documents to diagnostics.

Keep request identities and safe fingerprints for correlation. Bound retained decisions and grants. Clear session state with its owner.

## 13 Migration/Compatibility

This is an intentional default-deny behavior change for `run_process`.

Do not convert existing executable allow lists into broad command Allow rules. Keep `tools.allowedExecutables` as the existing outer-process ceiling.

The new rule outcomes replace the coarse shell approval Boolean for this feature. An old `requireApproval=false` must not bypass command rules. Do not leave the old default Boolean forcing approval on every new explicit Allow.

Preserve independently configured mandatory tool review. Define its combined approval behavior during T02. Display one prompt with the strictest permitted scopes.

Reject obsolete command-policy modes and source locations with migration diagnostics. Do not silently accept the earlier `denyList`, `allowList`, or `webAccess` proposal.

Do not rewrite user configuration automatically. Document the shipped Git/.NET allows. Show users how to add exceptions or approval requirements through the shared configuration path. Do not require approval rules to enable the normal workflows in section 6.10.

Do not restore live session grants from resume or clone data. Re-resolve current machine/user policy.

## 14 Acceptance Criteria

- [ ] No matching rule means denial, without an approval prompt.
- [ ] Each configured outcome has the exact semantics in section 6.1.
- [ ] Machine enforcement cannot be relaxed by user rules or grants.
- [ ] Only machine/user sources can define this feature's policy.
- [ ] Platform and shell selection use actual host execution facts.
- [ ] Every launched command retains its authorized executable identity and command kind. Unbindable forms are denied before launch.
- [ ] Every supported OS/shell pair has real-shell shadowing evidence. Windows `cmd` coverage includes repository-local `git.exe` shadowing of `git status`.
- [ ] Rules support composable typed matchers and future host-owned matcher additions.
- [ ] Invalid configuration, matcher errors, and unsupported syntax fail closed.
- [ ] Permission-bearing rules account for all arguments and supported shell effects.
- [ ] Explicit overrides are deterministic and independent of configuration order.
- [ ] Shipped rules allow the normal Git/.NET baseline in section 6.10, including Git network operations and .NET restore and package operations.
- [ ] Builds and tests can use implicit restore. This feature adds no mandatory no-restore flags or approval prompts to the shipped Allow rules.
- [ ] Specific exceptions do not become blanket denials of normal Git/.NET workflows.
- [ ] Rule catalogs, grants, and detailed policy decisions stay out of model-facing content. Denials provide no alternative command or bypass advice.
- [ ] User-facing approval and management views show the details needed to make and revoke an informed approval.
- [ ] Direct HTTP and component-installation test requests never reach process execution under the shipped denials.
- [ ] All actions pass before any part of a compound request starts.
- [ ] Approval scopes are host-enforced and cannot be forged or replayed.
- [ ] Persistent approvals use the user `alwaysAllow` collection without overriding stronger policy.
- [ ] Every host-managed change to new policy lists uses one shared service and existing settings publication.
- [ ] Manual reloads use the same validation and effective publication path.
- [ ] Parent, child, interactive, and headless calls use the existing pipeline.
- [ ] The reusable library has no dependency on Tools, App, Core, or a frontend.
- [ ] Real-platform evidence and all remaining isolation limits are reported accurately.

## 15 Risks

| Risk | Required control |
|---|---|
| Shell syntax exceeds parser support. | Publish the support matrix. Deny unsupported forms. |
| Shell lookup selects a different executable. | Pin authorized paths with shell-safe syntax. Reject unbindable forms. Require real-shell shadowing tests. |
| An executable changes after authorization. | State the remaining file replacement race separately. Do not use it to excuse lookup mismatches. |
| Allowed binaries execute hidden code. | Accept the limit for normal workflows. State the non-sandbox limit. Review specific escape mechanisms. |
| Policy diagnostics enter model context. | Separate user and model projections. Test every model-visible route. |
| Pattern overlap creates accidental permission. | Use explicit overrides. Deny unresolved conflicts. |
| A matcher ignores extra arguments. | Require complete coverage and negative tests. |
| Configuration saves diverge from active authority. | Use one update service and binding publication. Report partial failure. |
| Approval state survives too long. | Bind grants to lifecycle and fingerprints. Test revocation. |
| New abstraction grows into a framework. | Implement only the current domain and small host-owned ports. |
| The new project creates dependency cycles. | Keep it a leaf. Test the exact reference graph. |
| Default deny breaks useful workflows. | Ship the required Git/.NET Allow rules. Test normal arguments. Never add an unmatched-action fallback. |

## 16 Documentation

During implementation, update documents only at their owning boundary:

- Add the shared policy architecture decision and dependency contract.
- Update configuration, permission-source, and `run_process` references.
- Document each matcher, override, approval choice, and revocation operation.
- Document platform/shell support, the shipped Git/.NET allows, and explicit prohibitions for users.
- Keep policy catalogs out of model-facing documentation and deployed prompts.
- Add product acceptance scenarios and manual procedures for the new behavior.
- Record their stable IDs in this plan after allocation.
- Update prompt catalogs and references if prompt names or token contracts change.
- Add release notes for default deny and removed legacy behavior.

Do not reopen completed milestones. Keep the plans README as navigation only. Do not present this planned behavior as shipped behavior.

## 17 Open Decisions

The policy outcomes, default deny, source limits, composable matchers, new project, and shared update path are fixed. The normal Git/.NET Allow baseline and the model visibility restrictions are also fixed.

Resolve these bounded implementation choices in T02:

1. Select parser packages, supported grammar, and a verified execution-binding method for each OS/shell pair. Section 6.7 fixes the binding requirement.
2. Finalize serialized matcher names and approval record encoding.
3. Set and test input, rule, nesting, regex, and grant limits.
4. Finalize rule encodings and specific exception cases within the required section 6.10 baseline. Do not reopen normal Git/.NET network or package permissions.
5. Define composition with existing mandatory tool review without duplicate prompts.

Do not use these choices to restore a permissive fallback. Stop and report a conflict if an existing contract cannot preserve the fixed decisions.

### Plan preparation record

This revision is a documentation change only. Source inspection established the integration map. No policy code, configuration, project reference, or approval behavior was changed.
