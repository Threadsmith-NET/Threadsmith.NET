# Command policy authoring with a maintained native skill

**Status:** Active. Implementation is complete: the isolated validator, shared required dependencies and clarification lifecycle, maintained package and schemas, host repository/platform context, and observed-result checks are present. The ordinary manual completion projection shows the exact candidate and validation fields beside instructions. Supplied and observed examples remain required; only an exact example-set replacement supplied by an answered user clarification changes that baseline. Fresh-context review findings have been repaired. Final exit evidence remains limited to the available Windows environment; physical-terminal/provider authoring, other platforms, and the complete native release matrix remain unverified. Shared clarification uses the existing question coordinator and tool pipeline, releases execution admission while waiting, and preserves the remaining active-time budget.
**Delivery track:** M37 — Guided command policy authoring.
**Prerequisites:** Existing M3 tool policy, M12 skills, and M20 session contracts; ADR-11, ADR-34, and ADR-63; existing shell preparation.
**Date:** 2026-11-14.
**Writing standard:** ASD-STE100 Simplified Technical English. Identifiers, product names, and defined software terms are technical names.

**Acceptance contract:** [Scenario COMMAND-POLICY-AUTHOR](acceptance-scenarios.md#scenario-command-policy-author--guided-command-policy-authoring).
**Manual procedure:** [MTP-COMMAND-POLICY-AUTHOR](manual-test-plan.md#mtp-command-policy-author--guided-command-policy-authoring).
**Milestone contract:** [M37](milestones/milestone-37-guided-command-policy-authoring.md).

This revision replaces the local modal design.
The linked milestone, acceptance scenario, manual procedure, ADR-34, and ADR-63 define the native skill design.
Keep those contracts aligned during implementation.
Keep their stable identifiers.

## 1 Objective

Help the user create command policy JSON through a maintained native skill.
Use an LLM to interpret requirements, prepare a draft, and explain its scope.
Use a native tool to check the draft without command execution.
Return the JSON and instructions for manual use in machine or user configuration.

Make required tools available only within the skill execution when normal session enablement is absent.
Keep all authorization checks in effect.
Put the supporting tool in a new catalog category named **Native Skill Tools**.

## 2 Architectural Context

Read these contracts before implementation:

- Root `AGENTS.md` and `docs/guardrails/portable-csharp-guardrails.md`.
- [Planning governance](planning-governance.md) and [shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).
- [ADR-11](../architecture/adr-11-central-tool-policy-pipeline.md): central tool policy.
- [ADR-34](../architecture/adr-34-governed-declarative-skills.md): governed skills.
- [ADR-63](../architecture/adr-63-shared-command-policy.md): shared command policy and shell binding.
- [Skill authoring](../skill-authoring.md) and [skill operations](../operations/skills.md).
- [Command policy reference](../operations/command-policy.md).

Keep skills as verified data packages.
Keep model calls, tool calls, cancellation, and recovery in their existing execution paths.
Keep parsing, compilation, and evaluation in `Threadsmith.Policies`.
Keep shell preparation in `Threadsmith.Tools`.
Do not add a separate skill runner, policy store, shell parser, or authoring modal.

ADR-63 currently restricts detailed policy data to local user surfaces.
The new tool must report only on supplied draft data and prepared example facts.
It must not disclose active policy rules or active authorization decisions.
Record this distinction in the architecture contract before implementation.
Also record the new required-tool enablement rule in the skills contract.

### 2.1 Rationale for native skill contracts

Declared input and output schemas and execution-scoped tool dependencies give Threadsmith Native Skills an explicit execution contract.
The schemas already exist.
This plan adds execution-scoped tool enablement.

Input schemas define required fields, types, permitted values, and limits.
Output schemas define the result structure for callers and later workflow steps.
The host checks outputs before the workflow can advance.
These checks improve consistency across executions and reduce reliance on prose instructions alone.
They do not make model results deterministic or prove that a policy has the intended effect.
Use the policy validator to check policy syntax and example behavior.

Execution-scoped tool dependencies are a distinctive Threadsmith Native Skills capability.
A native package can declare the tools that its procedure needs.
The host can then supply those tools for that execution.
The package does more than select tools already enabled for ordinary conversation.

Treat required tools as dependencies, not as setup instructions for the user.
For example, the user can invoke the command policy skill without first finding and enabling its validator.
Check required dependencies before model work starts.
Report a clear reason if the host cannot supply a dependency.
Use optional tools only when normal enablement and policy permit them.

Keep this capability in the shared native skill execution path.
Do not change saved settings or expand the outer conversation's tool catalog.
Keep policy, approval, and invocation checks in effect.
The package declares a dependency; it does not grant authority.

Document this distinction in the Native Skills overview and skill-authoring guidance.
Agent Skills Specification packages provide portable instructions and resources.
Threadsmith Native Skills add declared input and output schemas with host checks, and host-resolved, execution-scoped tool dependencies.
Together, these contracts define what a skill accepts, what it returns, and which tools it needs.
Describe these behaviors as Threadsmith host contracts, not as portable specification guarantees.
Do not claim that other hosts cannot provide a similar extension.

## 3 Scope

- Add execution-scoped enablement for native skill `requiredTools`.
- Keep `optionalTools` dependent on normal user enablement.
- Add the **Native Skill Tools** catalog category.
- Add one native tool for command preparation and candidate policy checks.
- Add one maintained native skill for conversational policy authoring.
- Accept natural-language requirements, command examples, and expected outcomes.
- Support the existing five policy outcomes and current policy schema.
- Return draft JSON, validation results, scope warnings, and manual merge instructions.
- Support ordinary manual, model-driven, and headless skill entry points.
- Preserve current command management and execution controls.

## 4 Non-Scope

- A guided modal or a new `/command-policy` authoring route.
- Execution of supplied commands, scripts, shell profiles, or discovery probes.
- Reading active machine or user policy files for the skill.
- Comparing a draft with active policy or reporting active policy decisions.
- Configuration writes, activation, reload, elevation, or saved approval grants.
- New policy matchers, positive argument grammars, or command-specific authorization code.
- Automatic removal of machine constraints or inherited rules.
- Tools that are hidden from ordinary users or restricted to one skill identity.
- Changes to Claude-style tool mappings or package trust requirements.
- A claim that example checks prove safety for every possible command.

## 5 Current State

These observations describe the current checkout.
Check the affected paths again before implementation.

| Area | Existing owner | Required change or reuse |
|---|---|---|
| Skill declarations | `SkillRequirementSet` | Reuse `requiredTools`, `optionalTools`, and `inheritAvailableTools`. |
| Compatibility | `SkillCompatibilityEvaluator` | Separate normal enablement from policy permission. |
| Tool selection | `SkillWorkflowOrchestrator.ResolveAvailableTools` | Extend selection for required tools outside the caller's advertised set. |
| Caller scope | Captured tool registrations and operation scope | Preserve caller restrictions and registration identity. |
| Tool execution | `IToolInvocationPipeline` | Use the normal policy, approval, scheduling, and activity path. |
| Tool category | `ToolDefinition.Category` and catalog projection | Add Native Skill Tools through the shared category path. |
| Skill package | `Threadsmith.Skills/MaintainedSkills` | Use the existing manifest, asset, schema, and verification format. |
| Command facts | `ShellCommandPreparation` | Reuse preparation without access to process launch. |
| Policy JSON | `PolicyJson` | Reuse strict parsing, serialization, and document limits. |
| Policy checks | `PolicyCompiler` and `PolicyEvaluator` | Compile supplied candidate layers and evaluate prepared actions. |

Current skill requirements do not enable tools.
Current nested skill selection also filters declared tools against the caller's captured registrations.
This plan changes those rules only for required-tool enablement.
It does not remove the caller's policy limits.

## 6 Proposed Design

### 6.1 Shared required-tool enablement

Apply the new rule to verified, enabled native skill packages.
Do not add a special branch for the command policy skill.
Package verification and enablement remain necessary before tool selection.

| Declaration | Behavior during skill execution |
|---|---|
| `requiredTools` | Make each registered tool available if host policy permits it. Normal session enablement is not required. |
| `optionalTools` | Include each tool only when the user has enabled it and host policy permits it. |
| `inheritAvailableTools: true` | Inherit normally enabled, permitted tools. Also include permitted required tools. |
| `inheritAvailableTools: false` | Include only permitted required tools and enabled, permitted optional tools. |

Required takes precedence if the same ID appears in both lists.
Keep existing tool contract version checks.
Reject the skill before model work if a required tool is missing or incompatible.
Report a bounded reason when a required tool cannot be supplied.

Separate a disabled catalog preference from an authorization denial.
Required declarations may override only the catalog preference.
They must not override `tools:deny`, deny-all policy, or a restrictive `tools:allow` list.
Keep trust, phase, child-agent restrictions, network consent, approvals, and budgets in effect.
An enabled package does not authorize arbitrary tool use.

### 6.2 Execution scope and recovery

Create the tool set through the shared catalog and operation-scope owners.
Do not temporarily change saved settings or the session's global tool set.
Do not enable the tool in the outer model request.
Do not expose it to unrelated concurrent executions.

A nested skill can receive a required tool absent from the outer advertised set.
Resolve that tool through the host registry under the caller's current authority limits.
Capture its exact registration for the skill's model request.
An absent advertisement is not a denial.
An absent registration is not permission to select another implementation.

Check the tool set at compatibility, model-request, batch-preflight, and invocation boundaries.
Preserve source leases, registration identity checks, and normal invalidation behavior.
Dispose execution-owned references on completion, failure, or cancellation.
Do not restore live authority from a checkpoint.

Use normal skill continuation and resume.
Recheck the exact package, current policy, registrations, and tool contract versions.
Recompute required and optional availability without changing global settings.
Fail clearly if a required tool is no longer permitted.
Preserve existing model selection, budgets, completed work, and recovery rules.

### 6.3 Native Skill Tools category

Add **Native Skill Tools** to the ordinary tool catalog.
Place the command policy validator in this category.
Use the existing category metadata, grouping, filter, details, and enablement controls.
Keep the skill package in the existing Native / Maintained skill catalog branch.
The category identifies supporting tools; it is not a new skill package format.

Register the validator as non-essential and disabled by default for ordinary conversation.
Users can enable it and invoke it without the skill.
Apply the same input checks and policy controls in both cases.
Do not restrict invocation to the maintained package.

Trace category use in policy, scheduling, configuration, and presentation.
Preserve existing limits when adding the category.
Do not create an unlimited concurrency group or additional authority through its label.

### 6.4 Deterministic command policy tool

Use the working tool ID `validate_command_policy`.
Confirm the final ID before package publication.
The tool accepts a bounded validation request.
It does not generate policy from natural language.

The request contains:

- Candidate command policy JSON and its intended machine or user layer.
- The current host platform, a supported shell, and an explicit working directory.
- Complete command examples with stable IDs and expected outcomes.
- An explicitly supplied outer executable list, when that list is to be checked.

Permit preparation without candidate JSON so the skill can obtain typed command facts before drafting.
Return preparation status only in that case.
Do not report policy validation success without a candidate.

Compile the candidate against an explicit empty baseline and the selected layer.
Do not load active policy sources or silently include installed rules.
A missing rule therefore remains unmatched in this isolated check.
Explain that integration with installed defaults and other layers is not checked.
Reject override targets absent from the supplied candidate.
Do not invent rules to make compilation succeed.

Use `ShellCommandPreparation` before policy evaluation.
Reuse its parsing, native resolution, literal command names, and interpreter/script actions.
Resolve only the supplied shell and command targets.
Do not read script bodies, import modules, or launch a discovery process.
Keep process launch unreachable from this operation.

Use `PolicyJson`, `PolicyCompiler`, and `PolicyEvaluator` for candidate checks.
Evaluate every action and the complete command through existing combination rules.
Do not require an interpreter and its script to have the same outcome.
Report conflicts between examples that share actions.

Return structured results for:

- JSON parsing and policy compilation.
- Each prepared action and its candidate outcome.
- Each expected-outcome comparison and the complete command outcome.
- Unsupported syntax, unresolved targets, and unassessed checks.
- Outer shell eligibility against the supplied executable list, if present.
- Broader matcher scope and existing policy limitations.

For an expected denial, require an explicit matching denial rule.
Preparation failure, unsupported syntax, and default denial do not satisfy that expectation.
If the outer list is absent, mark its check as unassessed.
Do not require duplicate outer-list entries for commands inside the shell.

Attach a candidate digest and example-set identity to each validation result.
Identify the platform, shell, and relevant native bindings.
The result applies only to those inputs at that time.
It is not a grant, an execution test, or a check against active policy.

### 6.5 Maintained skill package

Use the working skill ID `command-policy-authoring`.
Ship it through the existing maintained package catalog.
Declare bounded input and output schemas and integrity-checked assets.
Make the skill input property `workingDirectory` optional.
Use the active repository root when the property is omitted.
Use this property description: "Working directory for command examples. If omitted, use the active repository root."
Resolve this default before tool invocation and pass the resolved directory explicitly to the validator.
Do not use the process working directory as the default.
If the repository root is unavailable, report the missing context. Do not guess a directory.
Use the ordinary model procedure and configured model.

Declare the validator and `ask_user` as required:

```json
"requirements": {
  "inheritAvailableTools": false,
  "requiredTools": ["validate_command_policy", "ask_user"],
  "optionalTools": []
}
```

These declarations do not enable a disabled skill package.
They do not permit tools missing from the host registry.
Do not declare general file, process, network, or configuration-write tools.

Provide static schema guidance through verified package assets or deployed prompt assets.
Do not fetch the schema or policy reference from the network at runtime.
If deployed prompts are used, follow the existing prompt catalog and startup cache contracts.

Include these instructions in the skill prompt:

> Use `ask_user` when information is unclear or missing and you need an answer to create or check the policy correctly.
> Ask related questions together through the normal tool.
> Do not guess the user's intended outcome, approval mode, or directory scope.
> Work with the user through repeated drafts and checks. Do not stop after the first draft or failed check if you can make a correction within the agreed requirements.
> Use validator results to correct the draft. Use `ask_user` when a correction needs a user decision or when requirements conflict.
> Keep the agreed requirements and examples through each revision. Do not weaken an expected outcome to make a check pass.
> Validate again after each change to the candidate or examples. Continue until the candidate meets the agreed requirements and retained examples, or a stop condition applies.
> If `workingDirectory` is omitted, use the active repository root. Do not ask for a directory only because this property is omitted.
> If the user declines a necessary answer, or the current surface cannot collect it, return `incomplete` and list the unresolved items.
> Also stop if the user asks to stop, a requirement cannot be supported, or the normal execution budget is exhausted. Do not repeat a failed attempt without a relevant change.
> Keep normal cancellation behavior. Do not claim validation if work is cancelled.

Use the existing `ask_user` tool and its normal input collection path.
Do not add a separate question service or private invocation path.
Required-tool enablement does not bypass the tool's question limits or invocation checks.
A clarification answer does not grant tool or policy authority.
If the host cannot supply `ask_user`, apply the required-tool admission rule in section 6.1.

#### 6.5.1 Skill input schema

Use this schema for skill invocation.
Only `request` is required at invocation.
Collect missing shell, layer, or expected outcomes before candidate validation.
Do not guess an approval mode.
Reject blank strings when a supplied field requires a value.

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["request"],
  "properties": {
    "request": {
      "type": "string",
      "minLength": 1,
      "maxLength": 8192,
      "description": "Requirements for the command policy."
    },
    "shell": {
      "type": "string",
      "minLength": 1,
      "maxLength": 1024,
      "description": "Shell used for command examples."
    },
    "workingDirectory": {
      "type": "string",
      "minLength": 1,
      "maxLength": 4096,
      "description": "Working directory for command examples. If omitted, use the active repository root."
    },
    "layer": {
      "type": "string",
      "enum": ["user", "machine"],
      "description": "Configuration layer where the user intends to apply the policy."
    },
    "examples": {
      "type": "array",
      "maxItems": 32,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["command", "expectedOutcome"],
        "properties": {
          "command": {
            "type": "string",
            "minLength": 1,
            "maxLength": 8192
          },
          "expectedOutcome": {
            "type": "string",
            "enum": [
              "Allow",
              "AlwaysDeny",
              "ApprovalRequired",
              "ApprovalRequiredSession",
              "ApprovalAlwaysRequired"
            ]
          }
        }
      }
    }
  }
}
```

Require an absolute path when `workingDirectory` is supplied.
Resolve an omitted directory from the active repository context, not from model text.
Pass the resolved directory explicitly to the validator.
The schema description does not set a runtime default by itself.

Keep example context separate from policy scope.
Do not add a directory matcher only because the examples use the repository root.
For example, a request to require approval for Git commands has no directory restriction unless the user requests one.
At the machine or user layer, that rule applies across repositories within the configuration's scope.
A request for this repository only requires an explicit directory matcher.

Assign stable example IDs when preparing validator requests.
Keep those IDs while revising the candidate.
Do not require the user to supply IDs or per-example directories.

#### 6.5.2 Skill output schema

Use one output shape for a checked candidate or an incomplete result.
Keep the policy as JSON text in `policyJson`.
The validator owns the policy structure; do not copy that structure into the skill schema.

Use the supported native skill schema subset.
It requires one type per node and does not support nullable type unions.
Omit `policyJson` or `validation` when unavailable. Do not use null placeholders.
Omit `actualOutcome` only when the validator cannot determine an outcome.

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["status", "warnings", "outstandingItems", "instructions"],
  "properties": {
    "status": {
      "type": "string",
      "enum": ["validated", "incomplete"]
    },
    "policyJson": {
      "type": "string",
      "minLength": 1,
      "maxLength": 65536,
      "description": "Candidate command policy subtree, not a complete configuration document."
    },
    "validation": {
      "type": "object",
      "additionalProperties": false,
      "required": ["candidateDigest", "exampleSetId", "results"],
      "properties": {
        "candidateDigest": {
          "type": "string",
          "minLength": 1,
          "maxLength": 256
        },
        "exampleSetId": {
          "type": "string",
          "minLength": 1,
          "maxLength": 256
        },
        "results": {
          "type": "array",
          "maxItems": 32,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["command", "expectedOutcome", "matched"],
            "properties": {
              "command": {
                "type": "string",
                "minLength": 1,
                "maxLength": 8192
              },
              "expectedOutcome": {
                "type": "string",
                "enum": [
                  "Allow",
                  "AlwaysDeny",
                  "ApprovalRequired",
                  "ApprovalRequiredSession",
                  "ApprovalAlwaysRequired"
                ]
              },
              "actualOutcome": {
                "type": "string",
                "enum": [
                  "Allow",
                  "AlwaysDeny",
                  "ApprovalRequired",
                  "ApprovalRequiredSession",
                  "ApprovalAlwaysRequired"
                ]
              },
              "matched": {
                "type": "boolean"
              }
            }
          }
        }
      }
    },
    "warnings": {
      "type": "array",
      "maxItems": 32,
      "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 2048
      }
    },
    "outstandingItems": {
      "type": "array",
      "maxItems": 32,
      "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 2048
      }
    },
    "instructions": {
      "type": "string",
      "minLength": 1,
      "maxLength": 8192,
      "description": "Manual merge instructions, or information needed to continue."
    }
  }
}
```

Apply these semantic rules in addition to schema checks:

- `validated` requires `policyJson` and `validation` for the exact returned candidate and retained examples.
- Require successful parsing, compilation, and preparation, at least one retained example, and a match for every retained example.
- Require no unresolved requirements and an empty `outstandingItems` array.
- Copy validation facts from the tool. Do not generate a digest or an outcome in model prose.
- Preserve a known `actualOutcome` when it differs from the expected outcome. Set `matched` to false.
- Set `matched` to false when an outcome is unavailable. Explain the reason in `outstandingItems`.
- Apply the explicit-denial rule in section 6.4. Default denial does not satisfy an expected denial.
- Use `incomplete` for missing answers, unsupported requirements, failed checks, or incomplete coverage. Return a draft if one exists.
- Include scope limits and unassessed checks in `warnings`. State that active policy was not read and installed-layer integration was not checked.

`validated` means isolated candidate validation only.
It does not mean authorization, activation, or a check against installed policies.
An `incomplete` policy result can be a completed skill execution.
Use the normal workflow completion contract; do not add a second success flag.
Map `responseProperty` to `instructions` for readable output.
Do not map policy validation status to `successProperty`.

These schemas set per-field limits.
Also apply the aggregate limits in section 6.8 and existing host limits.
A schema-valid payload can still exceed an aggregate limit.
Reject excess input and report output limits without silent truncation or a false validation claim.

### 6.6 Authoring procedure

1. Collect the user's requirements and complete command examples. Use `ask_user` if missing or unclear information prevents a correct draft or check.
2. Confirm the shell, expected outcomes, and destination layer. Use the supplied working directory, or the active repository root if it is omitted. Confirm any requested directory scope for policy rules separately.
3. Prepare the examples through the validator when command facts are needed.
4. Draft policy JSON with the existing schema.
5. Validate the draft and compare all retained examples with their expected outcomes.
6. Correct failures that can be resolved within the agreed requirements. Use `ask_user` when a revision needs clarification or a user decision. Repeat drafting and validation after each change.
7. Return the exact checked draft, results, scope warnings, and manual merge instructions when the agreed requirements and retained examples are satisfied. If a stop condition applies, report the unresolved items instead.

Treat these steps as a collaborative loop, not as a single attempt.
A first validation failure is feedback for the next revision, not an automatic final result.
Keep the current draft, agreed requirements, and retained examples in the existing skill workflow context.
Do not add a separate conversation store or restart the skill for each revision.
Explain conflicts and supported alternatives before asking the user to choose.
Do not ask the user to resolve syntax errors that the skill can correct itself.
Do not claim that further revisions can satisfy an unsupported requirement.
Use the stop conditions in section 6.5 and the normal budgets in section 6.8.

Use these existing outcome values:

| User intent | JSON value |
|---|---|
| Allow | `Allow` |
| Deny | `AlwaysDeny` |
| Ask; permit a saved choice | `ApprovalRequired` |
| Ask; permit a session choice | `ApprovalRequiredSession` |
| Ask every time | `ApprovalAlwaysRequired` |

Explain the approval choices before resolving ambiguous intent.
Do not silently remove a failing example.
Identify any example the user removes from the request.
After any JSON or example change, obtain a new validation result.
Do not label an edited or unchecked draft as validated.

Explain matcher scope separately from example outcomes.
Ordinary rules do not positively match a complete argument list.
A rule can cover other subcommands and options.
Report unsupported distinctions instead of inventing matchers or presenting a broad rule as an exact-command grant.

Directory scope covers the working directory and its descendants.
It does not confine file arguments or later program behavior.
A script path rule does not validate script contents.
Do not describe literal alias permission as binding a fixed implementation.

If a draft cannot meet the request, report that result.
The model must not replace deterministic failures with claims of success.
Example success does not prove all-command coverage or general safety.

### 6.7 Output and manual application

Return structured skill output with readable instructions and the candidate JSON.
Include validation status, input identity, requested outcomes, actual outcomes, and unassessed checks.
Use the existing success and response field contracts where applicable.
Do not add a special final-answer interception path.

State that the result is an isolated candidate check.
State that active machine rules, user rules, and saved grants were not read.
Do not claim the result is ready for automatic activation.

Show the required `tools` merge location and policy `schemaVersion`.
Distinguish a policy subtree from a complete configuration document.
Explain rule-ID replacement, explicit overrides, and ordinary array replacement.
List outer executable additions separately.
Never advise replacing an existing array with an incomplete list.

Do not invent resolved machine or user configuration paths.
Use documented platform locations and identify any user-specific assumptions.
Explain reload after policy-only edits and restart after outer-list or shell changes.
Leave inspection of existing configuration and manual application to the user.

### 6.8 Bounds and cancellation

Limit each validation request to 32 examples and 64 KiB of command text in UTF-8.
Also enforce existing per-command, JSON, policy, tool-result, and skill-context limits.
Apply the smallest applicable limit before expensive work.
Reject excess input without silent truncation.
Return explicit coverage limits if results cannot fit.

Do not scan repository contents, command history, policy catalogs, or unrelated configuration.
Use the ordinary model-turn and tool-call budgets for revisions.
Propagate cancellation through all asynchronous boundaries.
Discard obsolete results after cancellation or repository replacement.

## 7 Public Contracts

Use host-owned records for requests, action summaries, validation results, and skill output.
Keep machine-readable status separate from explanatory text.
Use stable example IDs and bounded reason codes.
Do not parse display text to decide success.

Distinguish these states: preparation failed, compilation failed, outcome mismatch, validated candidate, and unassessed check.
Keep candidate identity separate from active configuration identity.
Do not return active policy catalogs, source revisions, or hidden denial details.

Use the normal native tool registration and schemas.
Keep the validator independent of the skill package.
The required-tool rule belongs to shared skill admission, not to the validator.
Do not expose terminal or model-provider types across subsystem boundaries.

## 8 Project/File Changes

| Project or file | Planned change |
|---|---|
| `Threadsmith.Core/SkillContracts.cs` | Clarify required, optional, and inherited tool semantics. Extend host-owned scope data only where needed. |
| `Threadsmith.Skills/SkillCompatibility.cs` | Check registered required tools without requiring normal catalog enablement. Preserve policy denials. |
| `Threadsmith.Skills/SkillWorkflowOrchestrator.cs` | Resolve execution-scoped required tools and recheck them during continuation and resume. |
| Shared tool catalog and operation-scope owners | Capture permitted registrations without changing the outer or global tool set. |
| Tool category contracts and catalog projections | Add Native Skill Tools with ordinary policy and scheduling behavior. |
| `Threadsmith.Tools` | Register the validator and reuse command preparation without launch. |
| `Threadsmith.Policies` | Reuse strict parsing, compilation, and evaluation. Add no separate policy engine. |
| `Threadsmith.App` | Compose the normal tool registration and maintained package assets. |
| `Threadsmith.Skills/MaintainedSkills/command-policy-authoring` | Add the manifest, instructions, schemas, and asset integrity data. |
| Prompt catalog and references, if used | Register and document the deployed skill prompt. |
| Existing skills, tools, policy, and integration tests | Add boundary tests and end-to-end coverage. |

Confirm final file names after tracing the existing owners.
Do not add a configuration loader or policy publication service for this feature.

## 9 Ordered Tasks

1. Revise the related planned contracts and record the architecture amendments in section 2.
2. Trace enablement, policy denials, caller snapshots, tool categories, continuation, and resume.
3. Define the shared required-tool rules and test the authority boundaries.
4. Implement execution-scoped selection through existing catalog and invocation owners.
5. Add Native Skill Tools and register the disabled-by-default validator.
6. Implement bounded preparation and isolated candidate validation without launch.
7. Add the maintained package, schemas, instructions, and integrity data.
8. Test manual, model-driven, headless, cancelled, and resumed executions.
9. Update skill, tool, policy, privacy, and user documentation.
10. Validate the final change set and report unavailable platform checks.

Do not start feature implementation while the related contracts still require a local-only modal.

## 10 Testing

Use deterministic tests with observable completion and explicit barriers.
Follow G-24 for cancellation, clocks, and mutable-state ownership.
Do not use sleeps, runner-speed deadlines, or retries until success.
Assert structured behavior rather than exact model wording.

### 10.1 Skill tool selection

- A normally disabled required tool is available only within the skill execution.
- A disabled optional tool is absent; an enabled optional tool is available when permitted.
- Required declarations work with either inheritance setting.
- Missing tools and incompatible versions stop admission before model work.
- Deny rules, restrictive allowlists, deny-all, trust, phase, consent, and approvals remain effective.
- Child restrictions cannot be bypassed by invoking a skill.
- Manual and model-driven entry points use the same required-tool rule.
- The outer catalog, saved settings, and concurrent executions remain unchanged.
- Registration changes, invalidation, cancellation, continuation, and resume retain normal safety checks.
- Existing native skills remain compatible within the revised contract.
- Claude-style mappings do not gain automatic required tools.

### 10.2 Tool and catalog behavior

- Native Skill Tools appears in the normal catalog with working details, filtering, and enablement controls.
- Direct validator use and skill use reach the same invocation pipeline.
- Category changes preserve policy, scheduling, and configured limits.
- All five outcomes, malformed JSON, unknown members, duplicate members, and rule conflicts are covered.
- Each action in scripts, sequences, and pipelines is evaluated.
- Explicit denial differs from default denial and preparation failure.
- Unsupported syntax, missing targets, alias scope, and argument limits are reported.
- Outer executable checks use supplied data only; missing data remains unassessed.
- Input and result limits reject or report excess data without silent omissions.

### 10.3 Integration and privacy

Use a controlled model stub to test the complete procedure.
Cover drafting, validation, correction, missing input, budget exhaustion, and honest failure output.
Test more than one revision in a single skill execution: draft, failed check, correction, another check, user clarification, and final validation.
Check that a correctable first failure does not end the skill or require a new invocation.
Check that revisions retain agreed requirements and examples, and that user changes cause new validation.
Check that syntax corrections do not cause unnecessary questions.
Cover conflicting requirements, supported alternatives, declined necessary answers, user requests to stop, cancellation, and unsupported requirements.
Check that the skill stops honestly when no supported correction remains or a normal budget is exhausted.
Check that it does not repeat unchanged failed attempts or claim success from an obsolete validation result.
Test the input and output schemas in section 6.5 with the existing native schema validator.
Reject unknown fields, invalid outcomes, blank required values, and excess field or aggregate sizes.
Accept input with only `request`. Collect missing validation context through the normal `ask_user` tool path.
Check that the package declares both `validate_command_policy` and `ask_user` in `requiredTools`.
Check that the skill prompt directs the model to use `ask_user` for missing or unclear information needed for a correct draft or check.
Cover unclear approval intent, missing required context, declined answers, and surfaces that cannot collect answers.
Check that unresolved answers produce `incomplete` output without guessed requirements.
Check that missing or denied `ask_user` stops admission under the shared required-tool rule.
Check that omission of `workingDirectory` alone does not cause a clarification question when the repository root is available.
Check output with and without a candidate or validation result.
Check that incomplete policy results can complete the skill without a workflow failure.
Check that mismatched outcomes remain visible and unavailable outcomes cannot count as matches.
Check that `validated` requires the exact checked candidate, complete retained-example coverage, and no outstanding items.
Check that the `workingDirectory` property description states the repository-root default.
Check that omission passes the active repository root to the validator, even when the process working directory differs.
Check that a supplied working directory is used and that a missing repository root is reported when no directory is supplied.
Do not treat the default working directory as a request to add a directory rule to the policy.
Check that final JSON matches the candidate associated with the reported validation result.
Live model trials are supplementary evidence, not deterministic test gates.

Use recording boundaries to prove zero command launches, configuration writes, activation calls, and persistent grants.
Use canary values in active policy files.
Confirm they do not enter validator results, skill context, activity metadata, or restored workflow output.
Supplied examples and draft JSON may enter model context under normal skill rules.

Run focused tests and the applicable final solution checks from `CONTRIBUTING.md`.
Run the documentation preflight.
Record exact commands and results.
Report each untested OS/shell pair separately.
Static preparation tests do not establish real-shell execution behavior.

## 11 Security/Permissions

Treat requirements, examples, draft JSON, and package instructions as untrusted data.
The model may propose policy; it cannot grant permission or approve its own tools.
Keep active execution authorization separate from candidate validation.

Required-tool enablement changes catalog availability only.
It does not create file, process, network, secret, mutation, or approval authority.
Recheck normal policy on every invocation.
Never treat a skill manifest as a policy exception.

This design uses an LLM.
User-supplied requirements, examples, and drafts can leave the machine through the configured provider.
Normal model availability, latency, cost, retention, and privacy rules apply.
Do not promise local-only authoring.

Do not read active policy files for the model.
Do not return active rule matches or suggested ways around a live denial.
Return only supplied draft data and facts needed to check the supplied examples.
Use normal sanitization, safe rendering, and bounded serialization.

## 12 Observability

Use existing skill and tool lifecycle events, progress, logs, and completion projections.
Manual invocation uses the normal skill activity block.
Model-driven invocation uses the existing `invoke_skill` activity.
Show validator calls through normal tool activity handling.
Do not create a private status store or renderer.

Record bounded reason codes, counts, duration, cancellation, and invocation identity.
Do not add raw commands, candidate JSON, or file contents to operational metrics.
Keep supplied payloads within the existing governed context, result, and checkpoint policies.
Do not claim that normal model inputs and outputs are absent from all persistence.

## 13 Migration/Compatibility

Preserve `/command-policy` management syntax and current no-argument behavior.
Use existing skill discovery, inspection, invocation, and headless adapters for authoring.
Do not add a second entry path merely to retain the old modal proposal.

Keep existing manifest field names.
Document their changed semantics before release.
Assess all maintained and enabled native packages that declare required tools.
Those declarations will now request execution-scoped enablement.
Keep explicit package disablement and revocation authoritative.

Keep the command policy schema, layer precedence, array replacement, and override semantics unchanged.
Generated JSON remains a proposal until the user edits trusted configuration.
No active policy is changed by skill execution.

## 14 Acceptance Criteria

- A user can request a command policy draft through the maintained native skill.
- The skill uses the existing model, tool, input, progress, cancellation, and recovery paths.
- Required tools become available within the skill without normal catalog enablement.
- Optional tools remain dependent on normal enablement.
- Required declarations cannot bypass policy denials, approvals, consent, or child restrictions.
- Saved settings, the outer model catalog, and concurrent executions remain unchanged.
- The validator appears under Native Skill Tools and supports ordinary user enablement.
- The validator checks supplied candidate data without reading active policy or launching commands.
- Results identify prepared actions, expected outcomes, actual outcomes, and unassessed checks.
- The skill returns checked JSON, scope warnings, validation limits, and manual merge instructions.
- The skill declares `ask_user` as required and uses it when missing or unclear information prevents a correct draft or check.
- The skill works with the user through repeated drafts, checks, and necessary clarifications within one execution. A correctable first failure does not end the attempt.
- Each revision preserves agreed requirements and retained examples and receives a new validation result before a success claim.
- The skill respects user stop requests, cancellation, normal budgets, and unsupported requirements. It does not repeat unchanged failed attempts.
- Unresolved or unsupported requirements are reported instead of presented as successful validation.
- Authoring writes no configuration, activates no rules, and creates no persistent grants.
- Existing command management and execution behavior remain compatible.
- Required checks pass, with platform limits reported separately.

## 15 Risks

| Risk | Required control |
|---|---|
| A required declaration is treated as permission. | Separate catalog enablement from policy and enforce both at invocation. |
| A nested skill escapes caller restrictions. | Preserve authority limits and exact registrations when adding required tools. |
| Temporary enablement changes global state. | Build an execution-owned tool set; never toggle session settings. |
| The new category changes scheduling or policy. | Trace all category consumers and preserve existing controls. |
| The model invents valid-looking policy or overstates coverage. | Use deterministic checks and identify the exact checked draft. |
| A broad rule is described as an exact-command grant. | Explain matcher scope and reject unsupported distinctions. |
| Candidate checks are confused with active policy checks. | Use isolated inputs and explicitly list unassessed integration. |
| Private policy reaches the model. | Exclude active policy sources and test with canaries. |
| Preparation accidentally launches a process. | Keep launch unreachable and assert zero launch calls. |
| Manual merging removes existing access rules. | Explain replacement semantics and avoid incomplete arrays. |

## 16 Documentation

Revise the linked M37 detail, acceptance scenario, and manual procedure before implementation.
Replace modal requirements with skill invocation, scoped tool enablement, and isolated draft checks.
Keep stable scenario and procedure IDs.
Keep milestone lifecycle status only in `milestones.md`.
Do not reopen completed milestone contracts.

Update these documents during delivery:

- `docs/skill-authoring.md`: required, optional, and inherited tool semantics.
- `docs/operations/skills.md`: scoped availability, denials, caller restrictions, and recovery.
- `docs/operations/command-policy.md`: authoring, candidate checks, scope limits, and manual application.
- `docs/user-guide.md`: skill invocation and Native Skill Tools.
- Relevant tool catalog documentation: direct validator enablement and use.
- ADR-34 and ADR-63: the bounded contract changes described in section 2.

If a deployed prompt is added, update `docs/operations/prompts.md` and `docs/prompt-file-reference.md` in the same change.
Keep packaged help links valid under the documentation preflight.
Do not describe planned behavior as available before implementation.

## 17 Open Decisions

The product direction is fixed by this plan.
Use a maintained native skill, a normal validator tool, and shared execution-scoped required-tool enablement.
Use Native Skill Tools for the supporting tool category.
Do not restore the local-only modal design.

Confirm final internal type names and public IDs during implementation.
Complete architecture and related planning alignment before feature code changes.
Record any proposed scope change before implementation.
