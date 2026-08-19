# Plan 48 — Repository Model Selection and Reasoning Memory

**Milestone:** M17 — Claude-Style Skill Compatibility and Model Selection

**Prerequisites:** plans 07, 09, 18, 26, 29, 31–32, 35, and 46

**Depends on by:** future model routing, per-repository model policy, and interactive provider management

**Status:** Implementation complete; maintained real-terminal/restart closeout pending. The active-model authority, repository precedence/validation, atomic persistence, request-bound dispatch, `/models`, reasoning persistence/reset, context-inspection invalidation, documentation, and focused automated coverage are implemented.

## 1 Objective

Add an interactive `/models` command that presents the effective configured model catalog through the same keyboard-friendly selection experience used for solution selection, switches the current session to the selected provider/model, and atomically persists that selection plus its effective reasoning level in repository `.threadsmith/config.json`.

Repository selection is authoritative when present. User-catalog `defaultProviderId` and `defaultModelId` are startup fallbacks only when the repository has no model selection. A model switch must immediately update provider routing and status truth, invalidate or recompute context-capacity projections against the new model, and preserve the current reasoning level only when the new model supports the exact same host-owned level. Otherwise the host selects and persists `ReasoningLevel.None` and tells the user how to choose a supported level with `/reasoning`.

## 2 Architectural Context

Plan 31 loads immutable user and repository provider catalogs and currently treats the catalog's `defaultProviderId`/`defaultModelId` as startup selection. Plan 29 established safe nested settings persistence in repository `.threadsmith/config.json`, including atomic same-directory replacement, unrelated-property preservation, case-insensitive property matching, path confinement, and nonfatal preference-write diagnostics. Plan 26 projects the effective model, reasoning, and context usage in the composer-adjacent status surface. `SessionModelPreferences` currently resets reasoning to `None` when a resolved profile changes, while provider composition and `ConversationalShell` retain a startup profile id rather than a mutable active selection.

Runtime selection therefore requires one host-owned active-model boundary shared by TUI, headless/context assembly, model resolution, provider dispatch, reasoning state, and status. Updating only the TUI field would create a split brain in which the label changes while requests continue using the startup provider. M17 must change the authority once, publish immutable snapshots to consumers, and ensure in-flight work retains the model snapshot with which it began.

Repository settings are selection data, not provider definitions. Provider endpoints, secrets, capabilities, context windows, pricing, retries, and wire mappings remain in bounded provider catalogs. `.threadsmith/config.json` stores only stable provider/model ids and the host-owned reasoning level.

## 3 Repository Settings Contract

Persist this nested shape in `<repository>/.threadsmith/config.json`:

```json
{
  "model": {
    "providerId": "ollama-cloud-node2",
    "profileId": "3f03cf65-d8d4-5e0d-afac-9ed113ffc2ea",
    "reasoningLevel": "medium"
  }
}
```

`profileId` is the stable host `ModelProfileId`, not the provider's wire model name. `providerId` must identify the effective catalog binding that owns the profile. `reasoningLevel` uses the closed host `ReasoningLevel` taxonomy and must be supported by that effective profile.

The loader distinguishes:

- **absent selection:** `model.providerId` and `model.profileId` are both absent; user-catalog defaults may apply;
- **complete valid selection:** both ids resolve to the same enabled effective binding; repository selection applies;
- **partial, malformed, mismatched, disabled, or missing selection:** repository intent exists but is invalid. Do not silently fall back to user defaults. Report a bounded actionable diagnostic and require correction through `/models`, an explicit headless override, or manual settings repair;
- **missing reasoning preference under a valid repository selection:** use `None`, persist it on the next explicit selection/reasoning change, and report the missing preference once rather than adopting the user model's default invisibly;
- **invalid or unsupported reasoning preference:** use `None` for safety, report the incompatibility and `/reasoning` choices, and repair the repository value atomically when writes are available.

Repository settings never add or redefine a provider/model and cannot introduce endpoints or secret references. Repository provider-catalog layering from Plan 31 remains separate and continues to enforce type invariance and secret restrictions.

## 4 Selection Precedence

For interactive and headless startup, effective selection precedence is:

1. an explicit validated session/CLI model override, when such an existing host-owned override is supplied;
2. a complete valid repository `model.providerId` + `model.profileId` selection;
3. user-level provider-catalog `defaultProviderId` + `defaultModelId`, only when both repository selection keys are absent;
4. existing deterministic no-default/ambiguity behavior.

A repository setting that exists but is invalid is not equivalent to absence and must not silently activate user defaults. Repository provider/model definitions may constrain whether a remembered selection remains valid, but they do not change the precedence rule.

`defaultProviderId` and `defaultModelId` remain user-catalog bootstrap defaults. `/models` does not rewrite `~/.threadsmith/providers.json` or repository `.threadsmith/providers.json`.

## 5 `/models` Interaction

Typing `/models` with no arguments opens a numbered/keyboard selector through the current `IConsoleSurface.SelectAsync` boundary, matching solution selection behavior and preserving Up/Down, Enter, cancellation, native scrollback, bulk paste, and no mouse capture.

Each bounded choice displays:

- current marker;
- model display name;
- provider display name/id;
- effective context window and maximum output tokens;
- effective reasoning capability (`selectable`, `always-on`, or `unsupported`) and supported levels;
- availability/disabled state only when useful for diagnostics; disabled models are not selectable.

The list is deterministic and grouped or sorted by provider display order then model display name with stable-id tie-breaking. Duplicate display names remain distinguishable. Selecting the current model is an idempotent success. Cancelling changes and persists nothing.

After selection, write a concise confirmation containing model, provider, context size, and effective reasoning. Do not display endpoints, secret references, pricing credentials, or wire request details.

Optional noninteractive `/models current` and `/models select <profile-id>` forms may be added for automation only if they use the exact same application boundary and validation. `/model` is not introduced as an alias in this plan; the documented command is `/models`.

## 6 Host-Owned Active Model Boundary

Add a provider-neutral application service, command/result contract, and immutable selection snapshot (names finalized during implementation) responsible for:

- enumerating enabled effective catalog bindings without exposing provider SDK/configuration implementation types;
- resolving startup precedence after repository configuration is available;
- returning the current provider id, profile, generation, reasoning, source (`explicit`, `repository`, or `user-default`), and context limit;
- atomically switching the active profile and reasoning preference;
- persisting repository settings through a confined workspace-owned settings writer;
- supplying selection snapshots to model resolution and configured-provider dispatch;
- notifying status/context projections after a successful switch;
- generation-fencing concurrent switches, catalog refresh, and in-flight requests.

Provider dispatch resolves the selected `ConfiguredModelDefinition` for each new request and creates/uses the correct adapter with just-in-time secret resolution. In-flight model calls, governed runs, mutations, validations, corrections, delegated workers, and resumable stages retain their captured model-selection identity until their existing safe boundary; `/models` must not mutate the provider beneath an active request. If current architecture cannot switch safely while work is active, the command waits for or rejects at the host-owned boundary with an actionable message rather than racing.

Interactive and headless consumers use the same authority. TUI state never owns provider selection truth, and a mutable configuration provider is not passed into terminal code.

## 7 Reasoning Transition and Persistence

A model switch evaluates the session's currently effective host `ReasoningLevel` against the selected profile's effective reasoning capability and supported levels:

1. If the selected profile supports the exact same host level, preserve it and persist that value.
2. Otherwise set the effective level to `None` and persist `none` in the same atomic repository update as provider/model selection.
3. Emit this message shape with the new profile's actual supported choices:

```text
The selected model does not support an equivalent reasoning level. Reasoning was set to none.
Use /reasoning <level> to select one of: none, low, medium, high.
```

The list contains only levels the selected profile can actually accept. For an always-on model, explain that reasoning is always on and `/reasoning` cannot change it rather than advertising false choices. For an unsupported model, report `none` without suggesting unavailable levels.

`/reasoning <level>` continues to validate against effective provider compatibility. After a successful change it atomically updates `model.reasoningLevel` in repository `.threadsmith/config.json`, preserving provider/model and all unrelated settings. Failed/cancelled reasoning changes do not persist. If the preference write fails, the host reports that the current session changed but restart persistence failed; it does not pretend success was durable.

The exact-level rule deliberately does not infer that `Minimal`, `Low`, `Medium`, and `High` are interchangeable across provider mappings. Plan 46's effective capability projection remains authoritative. `None` is the only fail-safe fallback; a newly selected model's configured default reasoning is not silently substituted.

## 8 Context Capacity and Usage Projection

A successful switch immediately updates the effective model context window used by new context assembly and status. Existing `ContextInspectionProjection` values describe a request assembled for a particular model/limit and must not be divided by the new model's limit.

Add model profile/generation and effective context-limit provenance to the latest-context status snapshot or otherwise generation-fence it. On switch:

- invalidate the displayed latest-request context numerator/percentage until context is reassembled for the new model, or synchronously recompute only when the existing governed context assembler can do so without a provider call and with identical policy inputs;
- show the new model's context limit immediately, with usage as unknown/pending rather than a stale percentage;
- assemble the next request under the new profile's context window and configured host cap, then publish a matching numerator, denominator, percentage, estimate marker, and model generation;
- trigger existing pressure-aware conversation retrieval/compaction when the new effective limit is smaller;
- never truncate durable conversation/archive data merely because the active model changed.

Cumulative session input/output token usage remains historical provider usage and is not reset or rescaled by a model switch. It must not be presented as current-context occupancy. Status updates remain serialized before the next composer and do not redraw concurrently with selectors or streaming.

## 9 Persistence and Atomicity

Reuse or generalize Plan 29's workspace-owned bounded JSON settings writer rather than duplicating ad hoc TUI file writes. It must:

- confine the normalized repository root and reject prohibited/reparse configuration paths under existing rules;
- parse bounded JSON and preserve unrelated properties plus existing case-insensitive spelling where practical;
- update `model.providerId`, `model.profileId`, and `model.reasoningLevel` as one logical atomic same-directory replacement;
- use strict UTF-8, deterministic scalar values, flush/durability behavior consistent with existing preference writes, cancellation before replacement, and temporary-file cleanup;
- serialize concurrent solution/model/reasoning/tool preference updates so one cannot lose another's changes;
- return a host-owned outcome distinguishing applied, unchanged, and session-applied-but-persistence-failed.

Selecting a model authorizes only this repository preference write. It grants no repository trust, provider trust, network permission, secret access, mutation approval, or tool capability.

## 10 Headless and Observability Behavior

Headless startup consumes the same repository preference and fallback precedence. If repository selection is invalid, return a stable diagnostic/non-success outcome unless an explicit valid override is supplied; do not prompt. Any headless selection command uses stable provider/profile ids and the same persistence/application service.

Record bounded secret-free events or diagnostics for selection source, old/new stable ids, context-limit transition, reasoning preserved/reset, repository persistence outcome, and failure reason. Do not log endpoint URLs containing sensitive material, secret references, prompt/context bodies, or model-provider SDK types.

Execution records continue to capture the model/reasoning snapshot actually used for each request. Historical records are never rewritten after a switch.

## 11 Ordered Implementation Tasks

1. Add nested repository `model.providerId`, `model.profileId`, and `model.reasoningLevel` contracts, validation, precedence, diagnostics, and documentation.
2. Generalize the Plan-29 atomic repository settings writer so solution, model, reasoning, and other preference updates preserve one another under concurrency.
3. Add host-owned active-model query/select commands, immutable snapshots, generation fencing, and provider-neutral catalog entries.
4. Refactor startup resolution so repository selection is evaluated after repository settings are available and user catalog defaults apply only when repository selection is absent.
5. Refactor configured provider dispatch/model resolution so each new request uses the current captured selection while in-flight work remains stable.
6. Add the `/models` selector using `IConsoleSurface.SelectAsync`, deterministic provider/model labels, current markers, cancellation, confirmations, and status refresh.
7. Make `/reasoning` persist successful changes to the same repository settings and add exact-equivalence preservation/reset messaging during model switches.
8. Generation-fence or invalidate context inspection on switch, immediately project the new limit, and ensure the next context assembly/compaction uses the new effective capacity.
9. Add shared headless selection/current behavior and stable invalid-repository diagnostics.
10. Add startup, precedence, persistence, concurrency, provider-routing, reasoning-transition, context-capacity, in-flight-boundary, TUI/headless parity, privacy, and real-terminal tests.
11. Update user guide, keyboard shortcuts, provider operations, configuration example/catalog, manual tests, milestone/scenario/ADR as needed, status, and DOX.

## 12 Acceptance Criteria

- `/models` opens a keyboard selector equivalent to solution selection and lists every enabled effective configured model with unambiguous provider/model identity and useful context/reasoning metadata.
- Selecting a model changes the provider/profile used by the next eligible model request, updates status, and atomically persists provider id, profile id, and effective reasoning in repository `.threadsmith/config.json` while preserving unrelated settings.
- Restarting the same repository restores its model and reasoning. Opening a different repository uses that repository's selection or, only when absent, the user catalog defaults.
- Existing user `defaultProviderId`/`defaultModelId` never override or silently replace a present repository selection. Invalid repository intent reports correction instead of falling back.
- Selecting the current model is idempotent; cancelling or selecting an invalid/disabled/stale entry changes nothing.
- An exact supported reasoning level survives a model switch. A non-equivalent level becomes `None`, is persisted atomically, and produces an actionable `/reasoning` message listing only valid choices or explaining always-on/unsupported behavior.
- Every successful `/reasoning` change is persisted at repository scope; failure to persist is reported without falsely claiming restart durability.
- Context status never combines an old request estimate with a new model limit. The new limit appears immediately, percentage remains unknown until a matching reassembly, and the next request uses the new effective context cap and pressure/compaction policy.
- Cumulative session usage remains historically accurate and separate from current-context occupancy across switches.
- In-flight and resumable work retains its captured model/reasoning identity until a safe boundary; switching cannot splice two providers into one request/stage.
- Interactive/headless selection, precedence, persistence, validation, routing, and diagnostics use the same host boundary.
- No provider SDK type, endpoint secret, credential, or mutable configuration implementation leaks into Core events, repository settings, TUI contracts, status, logs, or durable projections.
- Focused automated coverage, architecture gates, Scenario R, maintained manual terminal/restart checks, docs, status, and DOX pass.

## 13 Test Plan

- Repository settings parsing for absent, complete, partial, malformed, mismatched provider/profile, disabled/missing profile, unsupported reasoning, unknown properties, case variants, bounds, and invalid UTF-8.
- Precedence matrix covering explicit override, valid repository selection, present-invalid repository selection, absent repository selection plus user defaults, and no defaults.
- Atomic persistence tests for create/update/idempotence, unrelated-property preservation, concurrent solution/model/reasoning/tool writes, cancellation, replacement failure, reparse paths, and restart.
- Selector tests for deterministic ordering, duplicate names, current marker, disabled omission, context/reasoning labels, cancellation, current selection, stale generation, and narrow terminal rendering.
- Provider-routing tests proving the selected binding—not merely the label—handles subsequent conversational, governed execution, correction, and relevant delegated requests.
- In-flight concurrency tests proving a captured request/stage is stable while selection changes and the next safe request sees the new generation.
- Reasoning matrix across selectable overlap, missing exact level, always-on, unsupported, legacy compatibility, persistence failure, and `/reasoning` restart restoration.
- Context tests switching larger-to-smaller and smaller-to-larger windows, host cap lower than either model, stale inspection invalidation, unknown/pending display, next-request reassembly, compaction pressure, and cumulative usage preservation.
- TUI/headless parity and privacy tests, plus maintained real-terminal selector, `Ctrl+C`, bulk-paste, resize, cancellation, footer refresh, cross-repository, and restart cases.

## 14 Documentation Deliverables

- User-guide and keyboard-shortcut documentation for `/models`, current markers, selection persistence, reasoning transition messages, and `/reasoning` persistence.
- Provider operations documentation distinguishing catalog defaults from repository selection and explaining invalid/stale repair.
- `.threadsmith/config.example` and configuration catalog entries for nested model selection/reasoning settings.
- Maintained manual tests for selector usability, context-window transitions, reasoning equivalence/reset, persistence, restart, and cross-repository isolation.
- ADR only if the active-model/provider dispatch boundary materially changes established model-resolution ownership.
- Scenario R, milestone/dependency/index/status updates, and DOX closeout.
