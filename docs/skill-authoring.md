# Declarative skill authoring

Threadsmith skills are bounded declarative packages, not executable plugins. Native packages may include integrity-checked C# script assets for use through the existing `csharp_script` tool; packaging does not execute them or grant authority. Packages cannot introduce assemblies, workflow expressions, arbitrary CLR type names, task/concurrency directives, or direct side effects. If a workflow needs a capability that does not already exist as a host tool/action, implement and govern that capability separately; a skill cannot create it.

## Package layout

Each catalog root contains one directory per package:

```text
my-skill/
  skill.json
  instructions/
    analyze.md
  schemas/
    input.json
    output.json
  scripts/             # optional, native packages only
    analyze.csx
    summarize.csx
```

Every file except `skill.json` must be declared exactly once in `assets` with package-relative path, exact UTF-8 byte count, lowercase SHA-256, required/optional flag, and kind. Undeclared files, rooted/traversing/alternate-stream paths, links/reparse points, hash/length mismatches, and manifest changes between discovery and verification invalidate the package.

Use `src/Threadsmith.Skills/MaintainedSkills/fix-analyzer-warnings/skill.json` as the maintained example. `scripts/create-maintained-skills.ps1` shows deterministic UTF-8-no-BOM asset generation and hash calculation.

## Manifest contract

`skill.json` schema version 1 declares:

- normalized `skillId.value`, distribution `packageId`, semantic `version`, display metadata, tags, publisher, and license;
- immutable assets and hashes;
- required/optional tool ids and minimum tool contract versions;
- minimum trust, approval disclosures, compatible host range, configured-model requirements, and aggregate budget;
- one acyclic `workflow` with a closed set of host-recognized steps;
- optional detached signature envelope.

Ids use lowercase ASCII letters, digits, `.`, `_`, or `-`. Versions use bounded semantic version text. Tool ids must already exist in the host registry. Required native tool dependencies can supply execution-scoped availability without changing session settings. Optional and inherited tools still require normal enablement. Requirements never add trust, network/process/secrets permission, approval, or budget; tool policy and consent remain authoritative.

## Native skill contracts

Native skills combine declared input and output schemas with host-resolved tool dependencies. Schemas improve structural consistency, not semantic correctness or model determinism. Required dependencies remove manual tool setup for that execution; they grant no authority and do not change the outer conversation's catalog or saved settings. Optional tools are not enabled implicitly. If a tool is both required and optional, required semantics take precedence. Missing, incompatible, denied, or consent-blocked dependencies stop model work rather than silently disappearing.

Portable Agent Skills Specification packages supply instructions and resources. The schema and dependency behaviors described here are Threadsmith host contracts, not portable specification guarantees or claims of exclusivity over other hosts.

### Optional native packaged scripts

Declare each script in the existing `assets` array with `kind: "script"`, exact UTF-8 byte count, lowercase SHA-256, and the usual required flag. Use flat `scripts/<filename>.csx` paths with forward slashes. Multiple scripts are supported; filenames are unique under case-insensitive comparison. Names use ASCII letters, digits, `.`, `_`, and `-`, and must not start with a dot. Nested folders, other script extensions, and scripts disguised as reference assets are rejected. Scripts are optional package resources; a package with no scripts works unchanged. Once declared, every script must pass integrity checks, regardless of its required flag.

The host supplies repository-contained scripts as repository-relative paths. On the first procedure invocation using an external package, it copies the verified script bytes into `skill-scripts/<session-id>/<package-digest>/` beneath the active scratchpad. Later steps in the same workflow reuse verified paths without rereading every script. New invocations and resumed workflows recheck file integrity; copies are never overwritten. Every selected packaged script is checked against its declared length/hash on the ordinary captured execution bytes before source evidence or worker execution, so changes between steps are rejected when used. Current path authority is checked on every step. The aggregate declared script size admitted for preparation is 4 MiB; available effective C# source byte/character limits also apply before copying. Standard scratchpad cleanup removes them. Missing copies are recreated after cleanup or session restoration. External staging requires an active scratchpad inside the repository and fails explicitly when unavailable. Discovery, verification, and installation do not stage or execute scripts.

The procedure's host-context JSON contains a `scripts` array with each filename in `name` (including `.csx`) and its repository-relative `scriptPath`. Instructions can refer to `analyze.csx`; the model uses that entry's exact `scriptPath` with the ordinary `csharp_script` tool and the appropriate `kind`. Script bodies are not automatically injected into the prompt. Bare tool filenames still select the scratchpad root, not the skill's script folder; use the supplied path rather than guessing one.

Declare `csharp_script` in `requirements.requiredTools` if execution is necessary. Scripts do not enable execution by themselves, bypass the scripting reference, or add filesystem, network, process, trust, approval, or tool authority. Existing source limits and worker restrictions apply. Keep scripts self-contained: no imports, `#load`, automatic dependency restore, shared worker state, startup hooks, or automatic execution. Claude-style compatibility packages are unchanged.

### Narrow authoring example

The maintained `command-policy-authoring` package declares `requiredTools: ["validate_command_policy", "ask_user"]`, `optionalTools: []`, and `inheritAvailableTools: false`. It uses one ordinary `invokeProcedure` step for the collaborative loop, maps `responseProperty` to `instructions`, and has no `successProperty`. An `incomplete` policy can be a completed procedure. Unavailable optional output values are omitted, not null. The host binds this maintained contract's validation claims to completed validator results; do not infer that arbitrary package schemas prove semantic correctness. See [guided authoring](operations/command-policy.md#guided-authoring-with-the-maintained-skill).

The ordinary procedure prompt includes host-owned repository-root and platform facts as JSON. A missing root is explicit; process cwd is not a repository default. Package input and text cannot replace host facts or grant authority.

## Safe JSON schemas

Every invocation/step boundary should declare explicit input and output schemas. The supported subset requires one `type` per node and supports bounded structural keywords:

- `object`, `array`, `string`, `integer`, `number`, `boolean`, and `null`;
- `properties`, `required`, `additionalProperties` (prefer `false`), and `items`;
- `minItems`, `maxItems`, `minLength`, `maxLength`, `minimum`, `maximum`, and `enum`;
- metadata-only `title` and `description`.

References (`$ref`), remote resolution, regex/pattern execution, custom formats, combinators, unknown keywords, excessive depth/properties/items/bytes, and dynamic types are rejected. Outputs are validated after model/tool work and before a workflow can advance.

## Workflow steps

Version 1 supports only:

- `invokeProcedure`, `collectEvidence`, `askUserInput`;
- `proposeDelegation`, `awaitDelegation`, `requestReviews`;
- `validate`, `summarize`.

Dependencies must form a bounded DAG. `maximumIterations` is a fixed positive ceiling and consumes the workflow/correction budget. Nested skill calls, arbitrary branches/expressions, recursion, unbounded loops, package-owned tasks/threads, and direct child-agent creation are prohibited.

Procedure instructions should state a narrow objective, evidence expectations, structured output, stopping condition, uncertainties, and meaningful validation. Do not claim host policy precedence, approval, trust, write authority, or successful validation. Such prose has no authority and adversarial claims are ignored by the host.

Host-action steps return typed requests under the existing closed action contract. Source edits use the ordinary shared edit command, exact authorization, transactional writer and advisory semantic feedback. Validation requires an explicit tool or host action; edit or response completion does not invoke it. Packages cannot revive removed planning or approved-execution actions.

## Signing and enablement

The package digest is SHA-256 over canonical manifest JSON with the `signature` value omitted; declared asset hashes are therefore covered by the signature. Sign the 32 digest bytes with ECDSA P-256 SHA-256 using DER sequence encoding and place this envelope in the manifest:

```json
{
  "signerId": "organization-key-2026",
  "algorithm": "ecdsa-p256-sha256",
  "signature": "<base64 DER signature>"
}
```

Trusted public keys are configured only outside repositories. Never put private keys or trusted-key configuration in a package. Signature verification establishes origin/integrity, not enablement or authority. An administrator/user must still enable the exact selector, or authorize an exact `digest|publisher|source` tuple. Revocation overrides both.

Unsigned packages may be enabled only through that exact external tuple. Repository configuration cannot self-allowlist.

## Portable Claude-style subset

Authors targeting both Agent Skills/Claude Code and Threadsmith may instead provide `.claude/skills/<name>/SKILL.md` using the pinned contract in [skill-compatibility-spec-v1.md](skill-compatibility-spec-v1.md). Keep the lowercase hyphenated directory and `name` identical, provide a bounded scalar `description`, use only closed mapped `allowed-tools`, and make the Markdown procedure instruction-only. Confined `.md`, `.txt`, `.json`, `.yaml`, and `.yml` references may be loaded as inert context.

Do not require hooks, forked contexts, subagents, dynamic shell injection, automatic script/binary execution, MCP assumptions, wildcard shell access, credentials, or repository-controlled enablement. Threadsmith reports such requirements as restricted or unsupported. Every source requires external exact-digest enablement and runs through the same governed model, tool, checkpoint, cancellation, and resume boundary; Claude-style format does not provide native signatures or workflow schemas.

## Import and test

Archive packages under one top-level directory. Import performs bounded non-executing ZIP extraction to same-volume quarantine, then complete verification and content-addressed atomic move. Do not include dependencies or expect automatic restore/network access.

Before distribution:

1. validate manifest bounds and schema version;
2. verify every byte count/hash from strict UTF-8 assets;
3. test metadata discovery while bodies are inaccessible;
4. test signature/exact allowlist, disabled state, tamper, revocation, and ambiguity;
5. test valid/invalid input and output schemas plus context pressure;
6. test cancellation/wait/continue/resume with the exact digest pinned;
7. test every requested action through ordinary tool policy, exact authorization and shared validation;
8. confirm events, logs, persistence, and diagnostic bundles contain no package body, secret, raw provider payload, or hidden reasoning.

See [skill operations](operations/skills.md), [ADR-34](architecture/adr-34-governed-declarative-skills.md), and the source-repository [skills implementation plan](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/plan-39-governed-skills-reusable-workflows.md).

## Editable native procedures

A model step may reference a code-declared deployed `promptFile` instead of a package `instructionAsset`. Prompt text comes from the normal startup cache; package schemas remain integrity-checked assets. `requirements.inheritAvailableTools` includes enabled session tools under normal policy, without requiring a separate hard-coded tool list.

Optional step `successProperty` and `responseProperty` name top-level output fields. The former must be a boolean and determines execution success; the latter must be a string and supplies readable output. Include them in the output schema. A false success value preserves the response and fails the workflow; explicit resume reruns that failed procedure. Findings do not themselves mean execution failed.

The maintained review skill uses these generic contracts with `prompts/Skill-Review.md` and ordinary `delegate_agents`. Agent roles and assignment count come from the procedure prompt and tool input; manifests do not define per-skill agent templates or agent limits. Trusted `agents:delegation:maximumAgents` configuration caps each delegation call. There is no private reviewer package format or special host entry.

Native procedures do not advertise recursive `invoke_skill` calls; recursion remains prohibited. Use the ordinary `delegate_agents` tool for child work.

A native procedure can declare `ask_user` as required and use its normal bounded questions when clarification is necessary. Model-driven skills borrow the conversation run; manual skills own and close a run through the same question coordinator. The host releases execution admission before presenting a question, pauses enclosing active-time allowances during human waiting, and revalidates policy, repository identity and registrations before continuing. Cancellation interrupts only the undelivered question, not a borrowed parent run. Clarification is unavailable to child agents or surfaces without a presenter. Answers never grant authority. `ask_user` and the enclosing `invoke_skill` must each be the sole call in their model batch.
