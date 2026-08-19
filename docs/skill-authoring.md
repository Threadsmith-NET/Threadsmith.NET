# Declarative skill authoring

Threadsmith skills are bounded data packages. They do not contain assemblies, scripts, expressions, arbitrary CLR type names, task/concurrency directives, or direct side effects. If a workflow needs a capability that does not already exist as a host tool/action, implement and govern that capability separately; a skill cannot create it.

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
```

Every file except `skill.json` must be declared exactly once in `assets` with package-relative path, exact UTF-8 byte count, lowercase SHA-256, required/optional flag, and kind. Undeclared files, rooted/traversing/alternate-stream paths, links/reparse points, hash/length mismatches, and manifest changes between discovery and verification invalidate the package.

Use `src/Threadsmith.Skills/MaintainedSkills/fix-analyzer-warnings/skill.json` as the maintained example. `scripts/create-maintained-skills.ps1` shows deterministic UTF-8-no-BOM asset generation and hash calculation.

## Manifest contract

`skill.json` schema version 1 declares:

- normalized `skillId.value`, distribution `packageId`, semantic `version`, display metadata, tags, publisher, and license;
- immutable assets and hashes;
- required/optional tool ids and minimum tool contract versions;
- minimum trust, approval disclosures, compatible host range, configured-model requirements, and aggregate budget;
- optional bounded Plan-38 role templates that remain proposals;
- one acyclic `workflow` with a closed set of host-recognized steps;
- optional detached signature envelope.

Ids use lowercase ASCII letters, digits, `.`, `_`, or `-`. Versions use bounded semantic version text. Tool ids must already exist in the host registry. Requirements can narrow host selection but never add availability, trust, network/process/secrets, approval, or budget.

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
- `proposePlan`, `awaitPlanApproval`, `executeApprovedPlan`;
- `proposeDelegation`, `awaitDelegation`, `requestReviews`;
- `validate`, `summarize`.

Dependencies must form a bounded DAG. `maximumIterations` is a fixed positive ceiling and consumes the workflow/correction budget. Nested skill calls, arbitrary branches/expressions, recursion, unbounded loops, package-owned tasks/threads, and direct child-agent creation are prohibited.

Procedure instructions should state a narrow objective, evidence expectations, structured output, stopping condition, uncertainties, and meaningful validation. Do not claim host policy precedence, approval, trust, write authority, or successful validation. Such prose has no authority and adversarial claims are ignored by the host.

Host-action steps return typed proposals only. Repository changes still use Plan 37; delegation/review still uses Plan 38. A package must expect a durable wait followed by schema-validated host result continuation.

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

Do not require hooks, forked contexts, subagents, dynamic shell injection, automatic script/binary execution, MCP assumptions, wildcard shell access, credentials, or repository-controlled enablement. Threadsmith reports such requirements as restricted or unsupported. Every source requires external exact-digest enablement and runs through the same Plan-39 model, tool, checkpoint, cancellation, and resume boundary; Claude-style format does not provide native signatures or workflow schemas.

## Import and test

Archive packages under one top-level directory. Import performs bounded non-executing ZIP extraction to same-volume quarantine, then complete verification and content-addressed atomic move. Do not include dependencies or expect automatic restore/network access.

Before distribution:

1. validate manifest bounds and schema version;
2. verify every byte count/hash from strict UTF-8 assets;
3. test metadata discovery while bodies are inaccessible;
4. test signature/exact allowlist, disabled state, tamper, revocation, and ambiguity;
5. test valid/invalid input and output schemas plus context pressure;
6. test cancellation/wait/continue/resume with the exact digest pinned;
7. test every proposed action through ordinary Plan-37/38 policy and validation;
8. confirm events, logs, persistence, and diagnostic bundles contain no package body, secret, raw provider payload, or hidden reasoning.

See [skill operations](operations/skills.md), [ADR-34](architecture/adr-34-governed-declarative-skills.md), and [Plan 39](implementation-plans/plan-39-governed-skills-reusable-workflows.md).
