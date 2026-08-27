# Plan 46 Parity Fixture Specification

**Fixture-set ID:** `plan46-pi-reasoning-v1`

**Status:** Normative sanitized source for Plan 46 deterministic fixtures.

## Purpose

This checked-in specification is the durable source for the 14 motivating compatibility profiles referenced by Plan 46 and Scenario P. Implementation and tests must not read `~/.pi`, user configuration, or another application's runtime files. Version 1 was derived on 2026-08-10 from a local Pi catalog, then reduced to public model identifiers and reasoning-specific declarations; request mechanics were checked against `@earendil-works/pi-ai` 0.84.1. Endpoints, provider credentials, authentication settings, user paths, display names, costs, and unrelated sampling values were omitted.

The source catalog did not declare a response-delta field per model. Version 1 therefore makes the deterministic response contract explicit below instead of claiming that an unverified live endpoint shape came from Pi. Live compatibility remains a separate opt-in check.

## Closed fixture vocabulary

- `none`: no reasoning delta is accepted for the profile.
- `reasoning-content`: the deterministic SSE fixture emits reasoning at `choices[].delta.reasoning_content`; the adapter normalizes it only to `ModelChunk.Reasoning`. A separate generic regression fixture retains the existing `choices[].delta.reasoning` alias.
- `legacy-standard`: no explicit M16 object; preserve the pre-M16 direct lowercase `reasoning_effort` projection and unsupported-level clamp. This is a generic regression case, not one of the 14 parity rows.
- `standard-effort`: explicit M16 mode; omit the control for `None` and otherwise emit the selected lowercase Threadsmith level as `reasoning_effort`.
- `mapped-effort`: an explicit M16 mapping controls `reasoning_effort`.
- `chat-template`: an explicit M16 typed binary projection controls allowlisted `chat_template_kwargs` members. Pi's `qwen-chat-template` emits `enable_thinking` plus fixed `preserve_thinking:true`; source level maps determine selectable levels but do not add an effort field.
- `fixed`: exact allowlisted additions are always emitted and effort is not selectable.
- `always-on`: no reasoning control property is emitted.
- `unsupported`: no reasoning control property is emitted and only Threadsmith `None` is effective.

`None`, `Minimal`, `Low`, `Medium`, and `High` below are Threadsmith levels. Pi-only `off`, `xhigh`, and `max` source keys are recorded where relevant, but `xhigh`/`max` do not expand the provider-neutral `ReasoningLevel` enum.

## Version 1 profiles

| Fixture ID | Sanitized source model ID | Source reasoning declaration | Effective M16 control and exact mapping/addition | Response |
|---|---|---|---|---|
| `remote-deepseek-v4` | `deepseek-v4` | `reasoning=true`; `thinkingFormat=chat-template`; source map `off→"false"`, `medium→"high"`, `high→"high"` | `chat-template`, selectable `None/Medium/High`: `None→chat_template_kwargs { thinking:false, reasoning_effort:"false" }`; `Medium/High→{ thinking:true, reasoning_effort:"high" }` | `reasoning-content` |
| `remote-gemma-4` | `Gemma-4-31B-IT-FP8-Block-MTP` | `reasoning=true`; provider declares standard effort unsupported; no model map | `always-on`; emit no reasoning control | `reasoning-content` |
| `remote-nemotron-3-super` | `nvidia/NVIDIA-Nemotron-3-Super-120B-A12B-NVFP4` | `reasoning=true`; fixed `LLM_ENABLE_THINKING=true`, `LLM_REASONING_BUDGET=4096`; provider declares standard effort unsupported | `fixed`; emit those two exact top-level additions; report uncontrollable | `reasoning-content` |
| `remote-qwen-3-5` | `Qwen/Qwen3.5-122B-A10B-FP8` | `reasoning=true`; `thinkingFormat=qwen-chat-template`; source map `off→"false"`, `high→"true"` | `chat-template`, selectable `None/High`: emit `chat_template_kwargs { enable_thinking:false, preserve_thinking:true }` for `None` and `{ enable_thinking:true, preserve_thinking:true }` for `High` | `reasoning-content` |
| `remote-qwen-3-6-nothink` | `Qwen/Qwen3.6-27B-FP8-NOTHINK` | `reasoning=true`; `thinkingFormat=qwen-chat-template`; only source map `off→"off"`; standard effort unsupported | effective `unsupported`; emit the typed fixed disable `chat_template_kwargs { enable_thinking:false, preserve_thinking:true }`; only `None` is valid | `none` |
| `remote-qwen-3-6` | `Qwen/Qwen3.6-27B-FP8` | `reasoning=true`; `thinkingFormat=qwen-chat-template`; source map `off→"off"`, `low→"low"`, `medium→"medium"`, `high→"high"` | `chat-template`, selectable `None/Low/Medium/High`: emit `chat_template_kwargs { enable_thinking:false, preserve_thinking:true }` for `None`; every enabled level emits `{ enable_thinking:true, preserve_thinking:true }`; the raw map restricts available levels but this Pi format has no effort-granularity wire field | `reasoning` |
| `remote-minimax-m2-7` | `MiniMax-M2.7-NVFP4` | `reasoning=true`; provider declares standard effort unsupported; no model map | `always-on`; emit no reasoning control | `reasoning-content` |
| `local-qwen-coder-7b` | `qwen2.5-coder:7b` | `reasoning=false` | `unsupported`; emit no reasoning control; only `None` is valid | `none` |
| `cloud-glm-5-2` | `glm-5.2:cloud` | `reasoning=true`; source map `off→"none"`, `high→"high"`, `max→"max"` | `mapped-effort`, selectable `None/High`: emit `reasoning_effort:"none"/"high"`; source `max` is deliberately unrepresentable | `reasoning-content` |
| `cloud-kimi-k2-5` | `kimi-k2.5:cloud` | `reasoning=true`; source map `off→"none"`, `high→"high"` | `mapped-effort`, selectable `None/High`: emit `reasoning_effort:"none"/"high"` | `reasoning-content` |
| `cloud-deepseek-v4-pro` | `deepseek-v4-pro:cloud` | `reasoning=true`; source map `off→"none"`, `high→"high"`, `max→"max"` | `mapped-effort`, selectable `None/High`: emit `reasoning_effort:"none"/"high"`; source `max` is deliberately unrepresentable | `reasoning-content` |
| `cloud-kimi-k2-7-code` | `kimi-k2.7-code:cloud` | `reasoning=true`; no model map; provider permits standard effort; Pi-compatible responses may use any known reasoning field | `standard-effort`, selectable `None/Minimal/Low/Medium/High`: omit for `None`; otherwise emit the direct lowercase level | `known-fields` |
| `cloud-kimi-k2-6` | `kimi-k2.6:cloud` | `reasoning=true`; no model map; provider permits standard effort | `standard-effort`, selectable `None/Minimal/Low/Medium/High`: omit for `None`; otherwise emit the direct lowercase level | `reasoning-content` |
| `cloud-minimax-m3` | `minimax-m3:cloud` | `reasoning=true`; no model map; provider permits standard effort | `standard-effort`, selectable `None/Minimal/Low/Medium/High`: omit for `None`; otherwise emit the direct lowercase level | `reasoning-content` |

## Fixture rules

1. The checked-in test catalog must contain each fixture ID above exactly once and declare `fixtureSetId: plan46-pi-reasoning-v1`.
2. Exact-body assertions include the compatibility-owned fragment plus host-owned `model`, `messages`, `tools`, `tool_choice`, streaming, token-limit, temperature, and response-format fields. Compatibility projection may change only the fragment stated above.
3. The separate `legacy-standard` regression fixture deliberately exercises absence of `reasoningCompatibility`; all 14 parity rows use their explicit control above and exercise fail-fast validation for unsupported levels.
4. Every `reasoning-content` row uses a fragmented deterministic SSE sample containing content, reasoning, tool calls, usage, and `[DONE]`. Tests assert that reasoning is neither duplicated as content nor written to any durable/general domain-event sink.
5. `max` mappings remain documented degradation because Threadsmith has no `Max` level. Session-affinity, long-cache-retention, generic sampling, and other Pi compatibility flags are outside Plan 46.
6. A future change to profile membership, mappings, additions, or response shapes creates a new fixture-set version. Do not silently mutate version 1 or regenerate it from mutable user configuration.
