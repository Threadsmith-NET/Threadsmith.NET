# Acceptance Scenarios (integration / E2E test specifications)

These stable scenarios are end-to-end product-behavior specifications. Active implementation documents cite applicable scenario IDs in the forward direction; this catalog does not maintain reverse work-item mappings, milestone attribution, implementation status, coverage status, or test counts.

---

## Scenario A — Read-Only Investigation

1. User opens a multi-project .NET solution.
2. User asks where an interface is implemented.
3. Harness loads the solution and resolves the symbol.
4. Roslyn finds implementations and references.
5. TUI displays files, projects, symbols, and provenance.
6. No model or shell command is required for the factual lookup.

**Verifies:** repository lifecycle, semantic discovery + symbol identity + references/implementations, semantic projection rendering in TUI, provenance carriage (§5.5), `SemanticConfidence` is at least `PartialCompilation` and surfaced (§13.x).

---

## Scenario B — Planned Code Change

1. User requests a bounded feature.
2. Harness gathers relevant semantic evidence.
3. Model produces a structured plan.
4. User reviews and approves the plan.
5. Model proposes a mutation set.
6. TUI displays exact diff and affected projects.
7. User approves application.
8. Harness applies changes transactionally.
9. Affected projects build.
10. Selected tests run.
11. User sees final diff, diagnostics, tests, and residual risks.
12. User accepts or rolls back.

**Verifies:** model abstraction, tool runtime for evidence collection and implementation, context governor + planning + plan approval, transactional workspace + diff + rollback, build + diagnostics + baseline/introduced classification, test selection/execution, mutation policy, and host-owned end-to-end orchestration/final evidence.

---

## Scenario C — Compilation Correction Loop

1. A mutation introduces `CS1503`.
2. Harness identifies it as introduced.
3. Diagnostic is correlated to the mutation and symbol.
4. Model receives only the relevant changed code, diagnostic, and contract.
5. Model proposes a corrective mutation.
6. Harness applies and recompiles.
7. Retry count and history are visible.
8. The loop stops at the configured budget.

**Verifies:** Roslyn semantic mutation, diagnostic normalization + baseline/introduced classification, selected-test correction, diagnostic→mutation correlation (`relatedMutationId` + `relatedSymbolId` at `≥ PartialCompilation` per §16.2), bounded-context-for-correction (§10.4 retry classification, §34-C budget stop), turn/visibility contract (§10.7) making the "introduced" classification authoritative at `FullSemantic` (§16.3), and reuse of the complete mutation/policy/transaction/validation path for each correction.

---

## Scenario C2 — Conversation-Native Corrective Turns

1. A model emits malformed tool arguments, an unavailable tool, or an invalid sibling in a multi-tool response.
2. The host rejects the invalid request before execution and does not repair arguments.
3. The next model request contains bounded corrective feedback controlled by `execution:maxCorrectiveTurns`.
4. A corrected request can proceed; exhausted attempts fail closed with sanitized diagnostics.
5. For MCP imported tools with provider-unsafe canonical ids, the provider wire name is safely aliased and mapped back before invocation.

**Verifies:** active-turn corrective history, atomic pre-execution batch rejection, purge after successful correction, safe diagnostics without raw malformed arguments/secrets/provider bodies, provider-neutral canonical tool identity, and OpenAI-family tool-name aliasing.

---

## Scenario D — Drop-In Extension

1. User copies an extension package into the configured directory.
2. Watcher signals a change.
3. Host waits for package stability and shadow-copies it.
4. Host creates a collectible load context.
5. Reflection finds a concrete `IThreadsmithExtension`.
6. Compatibility and permissions are reviewed.
7. Extension activates and registers a tool.
8. Tool appears in the Extension Manager and tool registry.
9. Model or user invokes the tool through the standard policy pipeline.

**Verifies:** abstractions package, discovery + collectible ALC + shadow-copy + `AssemblyDependencyResolver`, capability registry + invocation leases, extension→tool-runtime integration via the standard policy pipeline, file watching (§17.21), Extension Manager projection.

---

## Scenario E — Extension Unload

1. User disables an active extension.
2. Host stops new invocation leases.
3. Current calls complete or cancel.
4. Extension deactivates.
5. Host removes registrations and disposes extension-local services.
6. Host calls `Unload()`.
7. Weak-reference verification succeeds.
8. TUI reports `Unloaded`.

**Verifies:** lifecycle state machine + draining, cooperative unload procedure + `WeakReference` verification, `ExtensionDraining`/`ExtensionUnloaded` events (§9.4), lease semantics (§17.15).

---

## Scenario F — Blocked Extension Unload

1. A test extension subscribes to a static host event without releasing it.
2. User requests unload.
3. Host drains and initiates unload.
4. Weak-reference verification fails.
5. TUI reports `UnloadBlocked`.
6. Diagnostic information identifies likely retained registrations.
7. Host remains functional and recommends restart only when necessary.

**Verifies:** unload-blocker catalog detection (§17.18), `ExtensionUnloadFailed` event (§9.4), honest "restart may be necessary" UX (§17.19), host stays functional after an extension unload failure, mandatory leak-test fixture (§26.5).

---

## Scenario G — Hot Extension Replacement

1. A new package generation appears.
2. Host validates and activates it in a new load context.
3. Health check succeeds.
4. Capability resolution atomically switches to the new generation.
5. Old generation drains and unloads.
6. In-flight calls complete on the old generation.
7. New calls use the new generation.

**Verifies:** per-generation `AssemblyLoadContext` (§17.10/§17.20), `ExtensionGenerationId` (§9.1), atomic capability switch only after health check (§17.20 step 10), draining + unload of old generation, in-flight call completion on old generation (§17.15 leases).

---

## Scenario H — Responsive Terminal UI

1. Model streams output.
2. A build emits thousands of lines.
3. TUI remains responsive.
4. Output is coalesced and persisted to artifacts.
5. User opens diagnostics while the build continues.
6. User cancels.
7. Process tree and model operation terminate.
8. Session remains inspectable.

**Verifies:** TUI threading + background event dispatcher, bounded channels + backpressure + coalescing (§24.2–24.3, §18.9), process-tree cancellation, end-to-end cancellation including build, artifact persistence (§19.3), session remains inspectable after cancel.

---

## Scenario I — Cross-Turn Conversation Continuity and Compaction

1. In default Conversation-aware mode, the user establishes a requirement, corrects one assumption, approves a decision, and leaves one unresolved question across several turns.
2. The session accumulates enough additional complete turns and repository findings to cross compaction pressure.
3. The host archives every visible message, promotes typed memory, compacts older eligible turns at a turn boundary, and preserves source message/run/evidence provenance.
4. A repository mutation invalidates one remembered repository finding while leaving the user's requirement, correction, decision, and unresolved question active.
5. The user asks a later follow-up without restating the earlier details.
6. The assembled request preserves the current turn, includes a bounded recent window and structured summary, retrieves the relevant valid older memory, and excludes the invalidated finding.
7. Context inspection reports mode, included/omitted turns, summary categories/version, retrieval scores/rationale/provenance, compacted ranges, invalidation, supersession, and pressure reductions.
8. The user switches to Governed-memory-only; the next request contains structured/retrieved memory but no raw prior messages.
9. The user switches to Stateless; the next request contains only current input and current-run governed state.
10. The session restarts and restores archive order, effective mode, active snapshot, provenance, and deterministic retrieval.

**Verifies:** durable archive/memory contracts and restoration, validated compaction/retrieval/supersession/invalidation, three-mode assembly, token pressure, context inspection, configuration/session controls, and interactive/headless parity. Also verifies that hidden reasoning, secrets, raw tool output, unsupported assistant claims, and provider wire types never enter durable or assembled conversation memory.


---

## Scenario J — Interrupted Execution Resumes Safely

1. User approves a bounded plan and the host enters implementation.
2. A valid mutation proposal is staged against the current mutation baseline, its exact diff is recorded, and the exact pre-mutation affected workspace is built into a durable `BaselineCapture`.
3. The host process is deterministically interrupted after the durable `MutationStaged` checkpoint but before mutation approval.
4. The session is restored and reports the interrupted run, checkpoint, repository state, and one legal next action without applying the staged mutation.
5. User explicitly resumes; the host revalidates repository, solution, baseline, trust, policy, and artifact integrity, then presents the same exact diff for the required decision.
6. User approves; the host durably records the mutation-apply intent, the transaction applies exactly once, its result is reconciled, the mutation baseline advances to the resulting workspace generation, and the durable `MutationApplied` checkpoint is recorded.
7. The host is interrupted again after repository bytes change but before the apply result/checkpoint is recorded.
8. On resume, the host reconciles the pending operation against the expected result identity, records the already-completed application instead of reapplying it, classifies the post-mutation build against the preserved `BaselineCapture`, runs explained selected tests, and records one final outcome.
9. Repeat with repository bytes, selected solution, trust/policy, or a checkpoint artifact changed between interruption and resume.
10. Repeat cancellation before staging, while approval is pending, during commit, build, test, and correction; make the correction edit a file changed and a file created by the first set to prove it stages against the promoted mutation baseline.

**Verifies:** deterministic state and legal transitions, transactional/idempotent mutation and exact diff, pre-mutation `BaselineCapture`, build/test cancellation, and late-result abandonment, tolerant persistence/restoration, approval-policy revalidation, and separate diagnostic/mutation baselines, write-ahead side-effect intents, idempotent reconciliation, atomic checkpoints, explicit resume, no duplicate effects, and authoritative completion. Changed or corrupt state fails closed and requires a fresh plan/rebase path; cancellation preserves an inspectable safe repository state.

---

## Scenario K — Governed Reusable Skill Workflow

1. Organization, machine, user, and repository catalogs each expose bounded metadata for one or more skills without opening their instruction bodies.
2. User searches `/skills` for analyzer remediation and sees immutable version/digest, scope, publisher/source, verification state, tool/trust/model requirements, and compatibility or denial reasons.
3. A same-ID candidate exists in multiple scopes and a repository candidate attempts to shadow an organization-revoked package.
4. User explicitly selects the host-maintained `fix-analyzer-warnings` package by immutable scope/identity/version/digest; the host verifies its manifest and every declared asset and validates typed inputs and current requirements before loading bounded step content.
5. The workflow gathers authorized analyzer evidence and proposes a host plan; after plan approval it proposes exact mutations through M11.
6. User reviews the exact staged diff under the current mutation policy. The host applies authorized changes transactionally, builds affected projects, runs explained selected tests, and records authoritative results.
7. Cancel and restart at a durable workflow boundary, then explicitly resume.
8. Repeat with a tampered package, unsigned/unallowlisted digest, incompatible model, disabled required tool, insufficient trust, excessive schema/body, path traversal, prompt instructions claiming approval, and a revoked package between steps.
9. Invoke maintained `upgrade-package` and `review-pr` workflows against deterministic fixtures; authorize `review-pr` to propose delegated security, test, performance, and architecture reviewers, and verify the skill cannot create/schedule children itself.

**Verifies:** centralized tool and trust policy, governed context and structured plans, extension-versus-skill isolation, durable provenance/restoration, mutation policy/model compatibility, transactional execution/checkpointing, governed parallel-agent requests, and scoped metadata-first verified skills/workflows. No skill content grants capabilities, creates agents directly, or bypasses planning, scheduling/integration, exact-diff approval, transactions, validation, cancellation, or authoritative evidence.


---

## Scenario L — Bounded Parallel Research, Isolated Workers, and Review

1. In ordinary trusted chat with a selected semantic workspace, request two independent repository investigations and have the parent model call `delegate_agents` with exact `task`, `context`, and `readOnly` or `inherit` values.
2. Confirm the host freezes the parent's exact visible tool snapshot, rejects unknown knobs or excess children, removes mutation, process/code-execution, approval-required, workflow, and delegation tools, and narrows child trust, roots, prohibited paths, phase, sensitivity, network, and budget.
3. Two Explorer children run concurrently as in-process .NET tasks against one immutable baseline and return schema-validated cited findings; the joined result contains delegation/assignment IDs, honest statuses, uncertainty, omissions, conservative disagreements, and usage without raw child transcripts or hidden reasoning.
4. Inspect the latest durable state with `/agents <delegation-id>`, then cancel a child and a complete delegation in separate runs and confirm observed hierarchical cancellation. Through the shared persistence/checkpoint boundary, confirm accepted/queued/running and role-specific terminal revisions increase monotonically, joined state is durable before findings enter parent evidence, and a late lower-revision progress write neither replaces terminal state nor emits a stale lifecycle event.
5. User requests a multi-project change whose approved plan contains two provably non-overlapping implementation steps plus one shared configuration step.
6. The host proposes bounded read-only exploration assignments with explicit roles, questions, models, tools, trust ceilings, contexts, budgets, and stopping conditions.
7. The host synthesizes findings, approves/records the parent plan, partitions the two independent steps, and serializes the shared configuration step.
8. User approves the delegation projection. Two implementation children run concurrently in separate managed detached Git worktrees, each confined to its assignment and full governed mutation/validation path.
9. Freeze both structured worker change sets. Run security, test, performance, and architecture reviewers concurrently against immutable diff/evidence artifacts.
10. Resolve required review findings and ask the parent to integrate selected workers.
11. The parent detects worker-to-worker and current-primary conflicts, restages selected changes transactionally, presents one fresh exact aggregate diff, applies only after policy authorization, and reruns aggregate affected builds/tests.
12. Repeat with malformed child output, policy-denied child tools, overlapping/shared paths, stale primary bytes, an out-of-scope worker edit, cancelled parent/child, slow provider, exhausted child/parent budget, reviewer disagreement, interrupted checkpoints, and worktree cleanup failure.
13. Monitor operating-system processes while agents run.
14. During an ordinary model response and again during a two-child delegation tool batch, press Enter repeatedly. Confirm one immediate acknowledgement and one pause request; finish the in-flight operation, confirm the parent and every still-running child stop before their next provider/tool operation, and confirm no output occurs while `steer >` is visible. Submit ordered steering, verify eligible-child delivery and honest undelivered counts for completed children, then repeat with empty dismissal, buffered multiline input, `Esc Esc`, and `Ctrl+C` cancellation.

**Verifies:** deterministic state and immutable turns, direct model-callable bounded fork/join, repository/baseline/worktree confinement, exact parent-tool inheritance and central tool/context/capability/model/budget policy, validation and correction, persistence/restoration, serialized idempotent active-run input, safe-boundary parent/child steering, hierarchical cancellation, bounded in-process structured concurrency, non-overlap partitioning, isolated workers, typed reviewers, conflict-safe parent integration, and parent/child provenance. No process hosts a child agent; only existing tracked Git/build/test/tool infrastructure processes may appear. Children never delegate or transition parent workflow, unsafe partitioning falls back to serial execution, and no worker result is automatically merged.


---

## Scenario M — Governed Lifecycle Hooks and Managed Policy

1. A trusted user-level advisory HTTP handler and a repository executable handler declare `BeforeToolInvocation`; the repository handler remains disabled.
2. The user inspects the repository declaration and explicitly approves its exact repository identity, configuration digest, executable target, hook points, limits, secret-reference names, and advisory authority outside repository control.
3. A model proposes a read-only tool invocation. The host emits versioned envelopes in deterministic order, enforces bounded payload/data scope, and records advice from both handlers without allowing either to approve, rewrite, or block the tool.
4. Repository configuration changes the executable path or requests fail-closed behavior. The prior approval becomes stale, the handler does not execute, and repository content gains no blocking authority.
5. Trusted organization policy grants one immutable HTTP handler managed blocking authority for specified denial codes at `BeforeModelRequest`, `BeforeToolInvocation`, `PlanProposed`, `MutationStaged`, and `BeforeValidation`, with explicit fail behavior and secret/data scopes.
6. The handler denies one pending mutation by an allowed code. The host records the raw result, effective managed-policy decision, authority source, and legal blocked transition; it does not apply the mutation or treat the hook as an approval.
7. Repeat at an after/terminal point. The denial is advisory and cannot retroactively undo the completed host action.
8. Exercise typed executable, HTTP, already-connected MCP, and leased extension handlers with acknowledgement, advice, timeout, malformed/oversized output, cancellation, unavailable target, and secret-bearing configuration.
9. Trigger an MCP handler through the internal tool path and verify recursion suppression prevents tool-hook re-entry while retaining correlated audit evidence.
10. Interrupt immediately before and after a handler result and owning-operation checkpoint, restore the session, and verify no handler or host effect is blindly replayed or duplicated.
11. Revoke repository approval and managed policy, then compare equivalent interactive and headless list/inspect/test/audit results.

**Verifies:** immutable events and operation correlation, repository trust/identity, model/tool/context/policy boundaries, transactional mutation and validation boundaries, extension capabilities and leases, persistence/restoration/redaction, MCP adapters and connections, skills/workflows remaining distinct from hooks, and typed adapters, exact repository approval, advisory defaults, managed blocking authority, fail semantics, bounds, scopes, recursion prevention, auditing, and reconciliation.

---

## Scenario N — Typed Native Repository Investigation and Lifecycle Change

1. User opens a multi-project, multi-targeted repository with central package management, generated code, branches with a common merge base, analyzer findings, and parameterized tests.
2. The model uses typed `git_log`, branch comparison, `git_diff`, `git_show`, and `git_blame` tools to identify the relevant change history without invoking a pager, external driver, remote, or generic process tool.
3. A typed repository inventory reports solutions, projects, TFMs, project/package references, version sources, and test projects with revision provenance, confidence, and omissions.
4. NuGet health reports direct/transitive dependencies and bounded vulnerability/deprecation/outdated advisory evidence with source, freshness, and completeness; offline repetition performs no implicit restore or mutation and clearly reports incomplete advisory data.
5. Structured analyzer/build/format-check tools return normalized exploratory evidence. The user queries diagnostics by project, file, and code, discovers tests by stable host identity, and runs one targeted subset with the exact generated scope/filter shown.
6. The model requests incoming/outgoing calls and bounded impact for one symbol, runs a closed-schema C# pattern query, and inspects classified generated output. Results show dispatch/relationship reasons, workspace generation, confidence, dynamic unknowns, truncation, and provenance.
7. The user approves a plan requiring one file create, one move with an explicit content edit, and one delete. The model proposes typed lifecycle mutations rather than shell commands.
8. The host validates plan-step/path scope and source/destination baseline identities, stages an exact add/rename/edit/delete diff, classifies lifecycle risk, and obtains the configured mutation authorization.
9. The transaction applies once, invalidates semantic state, runs authoritative affected builds/tests through M11, and reports the distinction from earlier exploratory runs.
10. Repeat with invalid Git revisions, oversized histories/graphs, malicious option/filter/pattern input, stale package data, ambiguous tests, degraded semantic confidence, destination collision, reparse/secret/Git paths, cancellation, and interruption before/after each filesystem effect and checkpoint.

**Verifies:** repository/semantic/tool foundations, transactional mutation and validation, availability/provider/network policy, typed Git and inventory, NuGet/validation/diagnostic/test tools, advanced semantic inspection, and interruption-safe file lifecycle mutations. Ordinary high-value operations use typed contracts; read tools do not mutate; exploratory evidence cannot impersonate authoritative acceptance evidence; all writes remain host-governed and transactional.


---

## Scenario O — Download, Install, Upgrade, and Remove a Tagged Release

1. A maintainer creates an explicit release-candidate version from a clean tagged source commit. Pull-request validation has no signing credentials or repository-release upload authority.
2. Matching Windows, macOS, and Linux runners publish `Threadsmith.App` and `Threadsmith.Scripting.Worker` self-contained for every declared RID and assemble the canonical staged layout.
3. Each runner verifies required runtime, configuration, product and ripgrep license/provenance content; launches the staged application from a path containing spaces/non-ASCII characters; invokes the isolated scripting worker; and runs the exact RID-matched, checksum-pinned `tools/rg(.exe)` binary.
4. Windows produces a standalone setup executable, macOS produces architecture-specific installer packages, and Linux produces architecture-specific `.tar.gz` bundles with bounded install/uninstall scripts.
5. Clean ephemeral environments install the artifact, start a fresh shell, locate `threadsmith` through the documented launcher/PATH behavior, report the tagged version and architecture, and complete a headless smoke run without a separately installed .NET runtime.
6. The environments exercise scripting-worker launch, bundled literal repository search without relying on `PATH`, configuration-example availability, writable user-state separation, and extension probing from the installed layout.
7. Install the next compatible release over a previous fixture, verify stable product/package identity and preserved user configuration/data, then uninstall and confirm only installer-owned files and PATH entries are removed.
8. The aggregate release job verifies every artifact filename, embedded/reported version, source commit, size, manifest, SHA-256 checksum, and required signature/notarization state before attaching the complete immutable matrix to the tagged repository release.
9. Repeat with tag/version mismatch, missing or wrong-RID worker/ripgrep files, changed upstream ripgrep archive or license metadata, checksum/signature failure, unavailable credentials, duplicate assets, partial matrix failure, cancellation, unsafe install paths/symlinks, locked files, and an interrupted draft release; each case fails closed with documented recovery and no secret leakage.
10. Download each final attachment from the repository release page, verify its checksum, repeat the documented install/uninstall path, and confirm that no package-manager or external distribution channel is required.

**Verifies:** executable/runtime composition and CI foundation, operational security/redaction and diagnostics, isolated scripting-worker deployment, compiled provider/runtime composition, and canonical payloads, platform installers, signing hooks, provenance, immutable repository-release attachment, installed-layout smoke tests, upgrades, uninstall safety, and user-state preservation.


---

## Scenario P — OpenAI-Compatible Reasoning Compatibility

1. Load the repository-owned `plan46-pi-reasoning-v1` specification from `plan-46-parity-fixture-spec.md` and configure its 14 sanitized OpenAI-compatible profiles, covering legacy standard `reasoning_effort`, custom level mapping, fixed nested thinking enable/disable, always-on/uncontrollable reasoning, and unsupported reasoning.
2. Load the catalog and inspect each model's effective controllability, supported/default Threadsmith levels, compatibility mode/version, and response extraction mode without activating arbitrary types or reading Pi/user files.
3. Select every supported level, including explicit `None` behavior, and capture the exact outbound JSON. Confirm model, messages, tools, tool choice, stream settings, maximum output tokens, temperature, response schema, and credentials remain host-owned.
4. Stream deterministic content, reasoning, fragmented tool calls, and usage through each accepted response shape. Confirm reasoning becomes only bounded `ModelChunk.Reasoning`, content is not duplicated, and tool/structured-output behavior remains intact.
5. Switch among selectable, always-on, and unsupported models. Confirm session preferences reset/revalidate correctly and `/reasoning`, footer/status, context inspection, and headless output describe effective behavior honestly.
6. Persist and restore the session. Confirm only the host-selected level/effective metadata required for continuity survives; live reasoning display travels over a separate transient-only stream with no persistence, telemetry, hook, evidence, context, or diagnostic subscriber. Seed a pre-M16 `modelReasoningObserved` row, run the migration, and confirm its text is transactionally purged and restoration remains tolerant across an interrupted migration.
7. Repeat catalog loading with an unknown mode/version, incomplete mappings, unsupported selected levels in an explicit M16 mode, ambiguous `None`, protected-field collisions, duplicate/case-colliding keys, excessive depth/count/bytes, unsafe names/values, and repository discriminator changes. Every invalid configuration fails before provider activation or network I/O with bounded sanitized diagnostics.
8. Separately exercise malformed SSE fields, a response timeout, retry-eligible transport failure, retry exhaustion, and cancellation during streaming. Confirm each failure occurs at its actual runtime boundary, remains bounded and sanitized, follows the configured retry/cancellation policy, emits no partial durable reasoning, and never retries cancellation or a non-replayable partial response.
9. Load a pre-M16 catalog without compatibility settings and confirm its effective request behavior and stable provider/model/profile identity remain unchanged, including the legacy unsupported-level clamp to `reasoning_effort: "none"`; migration guidance is explicit and no user/repository configuration file is rewritten.

**Verifies:** provider-neutral model/reasoning/session contracts, configuration/secrets and persistence, status/context surfaces, polymorphic provider catalogs and compiled OpenAI-compatible isolation, and typed compatibility validation, protected request projection, response normalization, migration, redaction, and exact parity fixtures.


---

## Scenario Q — Claude-Style Skill Compatibility

1. Place an unchanged sanitized instruction-only skill at `.claude/skills/review-focused-change/SKILL.md`, and place a same-name skill plus a distinct skill in the documented user-level Claude skills root.
2. Refresh `/skills`. Confirm metadata-only discovery reports both scope-qualified same-name candidates, source format/version, digest, enablement, and compatibility without reading their instruction bodies or supporting resources.
3. Inspect the repository candidate. Confirm bounded frontmatter, mapped/unavailable tools, referenced resources, script/hook/agent assumptions, restrictions, and stable diagnostics are visible without treating metadata as permission.
4. Explicitly enable the exact repository source/digest outside repository control, invoke it without repackaging, and confirm the host revalidates generation/digest/trust/model/tools/budgets before loading bounded instructions and confined text/templates as provenance-labeled untrusted context.
5. Let the skill collect authorized evidence and propose a bounded change. Confirm planning, exact diff, approval policy, transactional lifecycle mutations, build/test validation, cancellation, and final evidence all remain owned by existing host boundaries.
6. Repeat through headless commands and `invoke_skill`; confirm candidate resolution, restrictions, context selection, outcomes, and denials match the interactive path.
7. Modify one eligible source byte between selection and invocation and again before resume. Confirm immutable identity changes and stale invocation/resume fails closed rather than running replacement content under the prior digest.
8. Repeat with traversal, absolute/resource URI, escaping/cyclic symlink/junction/reparse point, alternate data stream, case collision, malformed UTF-8, YAML alias/tag/duplicate/merge/recursion/coercion attacks, oversized metadata/body/resource sets, and a source-replacement race. Each fails closed with bounded sanitized diagnostics; a confined canonical link target is deduplicated under stable identity.
9. Add `allowed-tools`, an unmapped tool, shell scripts, hooks, MCP assumptions, and subagent/fork metadata. Confirm only semantically mapped tools remain subject to current policy; executable or unsupported requirements are reported restricted/unsupported and never execute or grant authority automatically.
10. Revoke/disable the digest and test native-package/name collisions plus organization deny policy. Confirm native signed verification remains distinct, higher policy wins, private bodies do not leak, and no Claude credentials/settings/transcripts/model configuration are imported.

**Verifies:** centralized tools/trust and context governance, persistence and exact external enablement, model and mutation policy, authoritative execution/delegation/lifecycle mutation, and pinned standard parsing, confined discovery/resources, immutable compatibility identity, tool adaptation, restriction projection, and shared invocation surfaces.


---

## Scenario R — Repository Model and Reasoning Selection

1. Configure multiple enabled user-catalog providers/models with different context windows and reasoning capabilities plus valid user `defaultProviderId`/`defaultModelId`; ensure the repository has no `model` settings. Start Threadsmith and confirm the user default is the fallback.
2. Run `/models`. Confirm a keyboard selector equivalent to solution selection shows a current marker, unambiguous provider/model identity, context/output limits, and effective reasoning capability without exposing endpoints or secret references. Cancel once and confirm nothing changes.
3. Select a different provider/model whose supported levels include the current exact reasoning level. Confirm the next eligible request uses that provider binding, status changes, the reasoning level is preserved, and `.threadsmith/config.json` atomically stores nested provider id, profile id, and reasoning while preserving unrelated solution/tool settings.
4. Restart the repository and confirm the repository model/reasoning wins over changed user defaults. Open another repository with no selection and confirm it uses the user fallback instead.
5. Select a model that does not support the current exact reasoning level. Confirm the switch persists `none`, prints that no equivalent exists, and shows `Use /reasoning <level>` with only the selected model's valid levels; repeat with always-on and unsupported models and confirm truthful specialized guidance.
6. Run `/reasoning <supported-level>`, restart, and confirm the repository reasoning preference persists. Force the settings write to fail and confirm session application versus restart durability is reported honestly.
7. Switch from a large to a small context model after a context inspection exists. Confirm status immediately shows the new limit but never divides the old estimate by it; usage/percentage is unknown pending matching reassembly, the next request applies the new effective cap and pressure/compaction policy, and cumulative session usage is unchanged. Repeat small-to-large.
8. Attempt a switch while a request/governed stage is active. Confirm the in-flight operation retains its captured provider/model/reasoning generation through its safe boundary and the next eligible request uses the new selection, with no mixed-provider request or stage.
9. Add partial, malformed, provider/profile-mismatched, missing, disabled, and unsupported-reasoning repository settings. Confirm present-invalid repository intent reports actionable correction and never silently falls back to user defaults; `/models` or an explicit valid headless override repairs it.
10. Repeat current/selection/persistence through headless surfaces and with stale catalog generation, concurrent solution/reasoning/settings writes, selector cancellation, restart, and cross-repository isolation. Confirm one host authority, atomic preservation, bounded diagnostics, and no SDK/secret leakage.

**Verifies:** provider-neutral model identity/resolution, context assembly and model-generation-aware inspection, persistence and repository settings, status/context projection, catalog binding/default precedence/provider dispatch, effective reasoning capabilities, and `/models`, runtime switching, exact reasoning transition/persistence, concurrency, and shared interactive/headless behavior.


---

## Scenario S — Operation Duration Display and Transient Activity

1. Start Threadsmith with no `tui:showOperationDurations` setting and a deterministic slow streaming model. Submit an ordinary request. Confirm transient `THINKING` appears with increasing total-turn elapsed time, updates no more than four times per second, does not print repeated transcript lines, and disappears before the first non-whitespace final answer.
2. Configure one deterministic built-in tool. Have the model request it after a controlled delay, execute it under fake monotonic time, and continue to a final answer. Confirm `THINKING` yields to live `TOOLS` elapsed time, one completion marker reports the authoritative tool-execution duration, and `THINKING` resumes at the original total-turn elapsed value rather than restarting.
3. Repeat with an extension tool and confirm stable host-owned source classification, identical policy/cancellation behavior, and no extension type in public/durable/TUI contracts.
4. Repeat through stdio, SSE, and streamable-HTTP MCP profiles with deterministic transport delay and one retry. Confirm live `MCP` activity, one MCP-specific completion/failure row with the remote logical-invocation duration, no duplicate generic tool row, and no endpoint/header/token/argument/result disclosure.
5. Stream whitespace-only content, reasoning chunks, tool-call framing, usage, fragmented visible output, and `[DONE]`. Confirm only first non-whitespace final answer ends resumed `THINKING`; the completed transcript contains no host-generated `THINKING` marker; `/thinking on`, `/thinking off`, `/thinking`, and `Ctrl+T` control live streaming of future sanitized reasoning without making prior scrollback removable or durable.
6. Exercise model failure, malformed output, tool/MCP failure, timeout, cancellation at each boundary, event-stream completion, status-renderer failure, and shell shutdown. Confirm live timers stop, tasks are observed, final host/tool outcomes render in order, the composer remains usable, and no console-gate deadlock or cursor artifact remains.
7. Set `tui:showOperationDurations` false in user configuration, then override true in repository configuration; reverse both values and restart each time. Confirm standard precedence, one option controls request/tool/MCP timing together, disabled mode retains activity/outcome words without duration or periodic redraw, and unrelated configuration is preserved.
8. Restore legacy tool events without duration/source and inject negative, overflowed, or impossible duration metadata. Confirm legacy output omits duration, invalid data fails or degrades with bounded diagnostics, and Threadsmith never fabricates `0ms` or guesses MCP identity.
9. Repeat under `NO_COLOR`, redirected/plain-text test surfaces, Windows/Linux/macOS terminals, SSH, and one multiplexer while selecting/copying transcript text, using `Ctrl+C`, pasting 10 KB/100 KB, resizing, opening selectors, and streaming. Confirm semantic/plain-text parity and no input, selection, scrollback, or responsiveness regression.
10. Inspect SQLite, conversation archive/memory, context inspection, model requests, hooks, logs, telemetry, and diagnostic bundles. Confirm no timer ticks or hidden reasoning are persisted/replayed; final tool/MCP duration aligns with its owning telemetry boundary; headless structured output remains compatible; no secrets or SDK/terminal types leak.

**Verifies:** ordered event/projection and cancellation boundaries, centralized tool timing/policy, tolerant persistence/restoration, MCP adapter and real transports, semantic/native terminal rendering and status, scalar configuration precedence, and timing ownership, bounded live updates, source-specific markers, and transient activity lifecycle.


---

## Scenario T — OpenAI Codex Responses/OAuth Provider and Authenticated Model Discovery

1. Start without Threadsmith Codex credentials. Confirm no stale cached Codex model becomes selectable, unrelated configured providers/defaults remain unchanged, and the bounded `/auth openai-codex` or headless login guidance is available.
2. Authenticate against the protected Codex resource and return multiple account models, including a model ID absent from every repository fixture. Confirm every distinct returned model is represented once with deterministic stable profile IDs, bounded metadata, and no hard-coded model-list update.
3. Complete independent Threadsmith browser PKCE login and repeat through the headless device-code flow. Confirm callback binding precedes browser launch, state/issuer/redirect/official-host validation succeeds, tokens are owner-protected outside repositories, and no Pi credential/configuration file is read or changed.
4. Select each Codex capability class and issue a deterministic streamed turn using exact sanitized fixtures. Confirm native Responses—not Chat Completions—normalizes content, transient reasoning, usage, completion, and errors through provider-neutral contracts.
5. Invoke a Threadsmith tool, stream fragmented Codex tool-call arguments, return a correlated result, and continue the model turn. Confirm one governed tool path, exact continuation correlation, no hosted-tool bypass, and no response/wire DTO in durable or public state.
6. Exercise supported reasoning settings and model switches. Confirm exact effective reasoning capability, generation-fenced dispatch, immediate context/status refresh, transient-only hidden reasoning, and unchanged cumulative historical usage.
7. Return a discovered model whose provider maximum output equals its context window and project a smaller request reserve. Confirm positive governed input capacity, total bounded input/output, and honest inspection of full window/provider maximum/request reserve.
8. Exercise expiry, concurrent refresh, restart, denied consent, malformed callback/token/stream, retryable failure, unsafe replay, cancellation, logout, and re-login. Confirm single-flight refresh, bounded retry, deterministic in-flight generation behavior, actionable sanitized failures, and no token resurrection.
9. Attempt repository endpoint/authority/scope/header/type changes, unapproved redirects, inline tokens, corrupted cache/catalog data, and unsupported future stream events. Confirm trusted compiled policy cannot be expanded, required unknown protocol fails closed, optional events remain bounded, and repair does not corrupt unrelated configuration.
10. Inspect provider catalogs, repository files, SQLite, archive/memory, prompts, hooks, logs, telemetry, diagnostics, errors, and process behavior. Confirm no access/refresh token, code, challenge/state URL, account identity, hidden reasoning, raw body, Pi path/configuration, or provider wire type leaks; repeat maintained live-account checks on Windows, Linux, and macOS.

**Verifies:** model-provider abstraction and dispatch, secrets/persistence, OAuth security patterns, status/context projection, compiled provider catalogs and isolation, reasoning compatibility, runtime model switching, and native Codex protocol, independent OAuth, authenticated dynamic discovery, and output-capability/reserve semantics.


---

## Scenario U — Cache-Optimized Context Generation

1. Capture a deterministic baseline request through each compiled provider. Confirm `/context` distinguishes logical unique content from estimated wire tokens and reports messages, textual/native tools, provider framing, reserves, stable-prefix tokens, and provider cache counters without inventing unavailable values.
2. Submit identical eligible native tools in different registration orders. Confirm canonical grouped definitions and JSON schemas are byte-identical, unrelated core tools retain their positions when one MCP/extension tool changes, and native providers receive no duplicate textual schemas; repeat through a legacy textual-only adapter. Include absent, non-null, and explicit-null schema defaults and confirm canonicalization preserves each distinct model-visible meaning.
3. Submit three ordinary turns. Confirm stable host policy and repository instructions lead, historical user/assistant messages are chronological and byte-stable, each new message appends, the current request is last, and hidden reasoning is absent.
4. Trigger a tool call and continuation without a policy transition. Confirm the original request prefix and eligible inventory remain byte-identical and only the assistant tool call plus correlated result append. Then change phase/tool legality and confirm a new safe cache family is assembled without exposing an illegal tool.
5. Create root and nested `AGENTS.md` plus prompt appends. Confirm canonical parent-to-child resolution for the working scope, stable bundle identity, sibling isolation, and precise invalidation after an applicable instruction change. Drop its watcher notification, then simulate watcher overflow/error/loss; confirm independent turn-boundary source-fingerprint revalidation prevents stale reuse and watcher failure conservatively invalidates the repository's instruction bundles. Edit an ordinary source file and confirm the bundle remains unchanged.
6. Repeat instruction resolution with traversal, escaping symlink/junction/reparse point, case collision, malformed encoding, oversized content, source-replacement race, and repository trust change. Confirm resolution fails or invalidates safely and repository text never overrides host authority.
7. Add equal-relevance and changing evidence. Confirm content-addressed IDs, deterministic tie-breakers/formatting, provenance preservation, reuse of unchanged blocks, and placement after the stable conversation prefix. Required current truth must replace stale evidence even when that reduces cache reuse.
8. Cross context pressure at a complete turn boundary. Confirm one deterministic summary generation replaces only its intended range, remains unchanged on subsequent below-threshold requests, records the cache-family transition, and is not re-summarized on every turn.
9. Exercise automatic-prefix, explicit-cache-control, stateful-continuation, and no-cache fake providers. Confirm capability limits and breakpoints are honored; opaque continuation references are bound to provider/model/session/generation/instructions/trust/tools/compaction and invalidate on every incompatible change.
10. Expire/reject/corrupt continuation state and restart, switch model, change trust/instructions, logout, cancel, and induce an unsafe partial replay. Confirm safe requests recover through semantically equivalent canonical stateless reconstruction, unsafe replay fails closed, opaque references never leak, and caching never changes outcomes, permissions, or durable authority.
11. Compare the measured baseline with repeated ordinary turns and tool continuations. Confirm provider-reported cached input, hit ratio, and labeled cost/latency estimates are attributed honestly; no unreported saving is presented as measured.

**Verifies:** provider-neutral model/tool/usage and context inspection, persistence and bounded conversation continuity, compiled provider isolation and exact request projection, governed execution/tool continuation, operation usage visibility, and canonical wire measurement, structured chronology, repository instruction bundles, deterministic evidence/compaction, provider cache controls, and recoverable stateful continuation.


---

## Scenario V — Interactive Session Lifecycle, Resume, and Clone

1. In a repository, complete several conversation turns that create sanitized archive messages, governed memory, evidence, usage, a context inspection, and a non-default valid model/reasoning selection. Record the active session ID, restart Threadsmith, run `/resume <session-id>`, and confirm the same conversation mode, memory/provenance, valid evidence, usage, model, reasoning, context limit, and paused execution status are reconstructed before the composer opens. Confirm no raw provider transcript or hidden reasoning is replayed.
2. Run `/resume` without an argument. Confirm a bounded newest-first repository-scoped keyboard selector equivalent to solution selection, with deterministic IDs, states, activity times, previews, model/reasoning markers, and current-session marker. Cancel and confirm nothing changes; select the current session and confirm idempotence; select another session and confirm every session-derived status changes atomically.
3. Supply a missing, malformed, unavailable, and other-repository session ID. Confirm bounded actionable diagnostics, no repository/trust/solution switch, no partial projection replacement, and the original session remains usable. Restore a legacy/future-schema session and confirm explicit partial/read-only behavior rather than fabricated state or a crash.
4. Change the current provider catalog so the persisted model remains valid but its reasoning level does not. Resume and confirm reasoning repairs visibly to `None` with exact supported guidance. Then disable/remove the persisted profile and confirm a selection-required state; repository defaults do not silently replace historical session truth and no model-backed request runs until corrected.
5. Run `/new`. Confirm the prior session is durably resumable, a new ID becomes active, repository trust/configuration/solution/tool/policy remains effective, and conversation, memory, evidence, run/checkpoint, usage, context inspection, transient activity, and continuation/cache state are empty. Submit a request and prove no prior-session content appears in its canonical model input.
6. Build a source session with multiple turns, compacted memory, valid evidence, usage, and model/reasoning, then run `/clone`. Confirm the source is checkpointed, a new top-level session is atomically activated, copied governed context has new session-local identities plus source provenance, and output includes a directly copyable `/resume <source-session-id>` line.
7. Add different turns to the clone, resume the source through the printed command, and add different source turns. Resume each again and confirm independent post-clone histories, usage, memory, evidence, and context generations with no automatic merge or propagation.
8. Inspect the clone's durable state. Confirm it excludes active runs, pending approvals, mutation transactions, worker leases, hook invocations, cancellation state, transient `THINKING`/reasoning, raw provider content, expanded secrets, credentials, and opaque provider cache/continuation handles. Interrupted execution/delegation state is historical/paused and cannot execute twice without its normal explicit reconciliation gates.
9. Attempt `/new`, `/resume`, and `/clone` during model streaming, ordinary tools, MCP, mutation, validation, hooks, skills, selectors, and delegated work. Confirm each waits for or rejects at a complete safe boundary, cancellation/failure before publication leaves the current session active, and no mixed model/session/context/status generation is observable.
10. After resume and clone, inspect the first model request. Confirm canonical stateless reconstruction, current repository instruction/trust/tool/policy revalidation, invalidation of stale context inspections/cache families, honest unknown/pending values where reconstruction lacks data, and no provider-owned continuation reuse.
11. Repeat direct resume, selector resume, new, clone, copy/paste of the return command, cancellation, resize, bulk paste, native selection, restart, and a large bounded catalog in a real terminal. Confirm responsive interaction, deterministic labels, monotonic transition timing, no completed `THINKING` row, and secret-free diagnostics.

**Verifies:** durable/tolerant persistence and conversation reconstruction, execution/delegation checkpoints, active model/reasoning and status truth, canonical cache-safe request recovery, and repository-bound catalog, atomic transition authority, `/new`, `/resume`, and independent `/clone` semantics.


---

## Scenario W — Host-Proven Parallel Tool Batches

1. Enumerate every effective production tool catalog/composition path. Confirm each current first-party tool and alias maps to exactly one reviewed scheduling descriptor and claim resolver covering direct/implicit resources, adapter thread safety, limits, approval/drain behavior, and a justified parallel/restricted/serialized decision. Add a tool, remove one, create a stale/duplicate entry, or force a generic unknown fallback and confirm the coverage gate fails.
2. Run table-driven claim fixtures for every current first-party tool and representative argument shapes. Confirm canonical claims change correctly for same/disjoint paths, repositories, solution/workspace generations, processes, hosts, MCP servers, extension generations, and session/global resources; verify explicit serialization for tools whose safety cannot be proven.
3. Script one model response containing two independent barrier-controlled repository-read tools. Confirm the complete sibling set is validated before either body starts, both bodies enter simultaneously before the barrier releases, observed peak concurrency is two, and batch elapsed time reflects overlap rather than serial awaiting.
4. Repeat with three or more independent calls under global, category, and source limits. Confirm actual peak concurrency never exceeds any effective cap, queued calls begin when permits release, timeout begins at admission, and limiter acquisition cannot deadlock.
5. Exercise same/disjoint read claims plus read/write, write/write, execute, external-effect, approval, session/global-exclusive, ancestor/descendant path, solution/project, Git-store, semantic-workspace, MCP-server, and extension-generation claims. Confirm only host-proven conflict-free calls overlap and unknown/malformed metadata serializes.
6. Randomize tool completion order across repeated runs. Confirm events/activity show real overlapping intervals and completion timing, while correlated model-visible results, evidence insertion, canonical continuation bytes, and the next request remain in original sibling-call order.
7. Deny policy, reject approval, exhaust aggregate budget, time out, throw, and cancel siblings under both supported batch failure modes. Confirm every started invocation reaches a bounded terminal result, queued calls obey cancellation, unrelated started work follows policy, and no permits, leases, processes, or activity rows leak.
8. Run approval-bearing siblings. Confirm prompts are never concurrent, appear in original order, one approval cannot authorize another invocation, and independent non-interactive work follows the deterministic wave plan.
9. Invoke built-in, stdio/HTTP MCP, extension, skill, hook-mediated, process, script, semantic, Git, and lifecycle-related tools. Confirm explicit reviewed behavior for each source; undeclared MCP/extensions and workflow/executable/code/mutation tools remain sequential.
10. Disconnect/reconnect MCP and drain/hot-replace an extension during a batch. Confirm one generation-fenced lease per admitted call, bounded drain, no new call against the retiring generation, and no false completion or safe boundary.
11. Attempt session/repository/model transition, mutation progression, and shutdown while a batch is queued or active. Confirm existing safe-boundary rules wait, cancel/drain, or reject without mixed state.
12. Inspect `/tools`, headless output, telemetry, and diagnostics. Confirm bounded effective concurrency and serialization reasons without raw arguments, paths, hosts, resource/lock keys, secrets, or result bodies.
13. Disable parallel batches and repeat the fixture. Confirm compatibility mode executes sequentially with equivalent ordered results. Re-enable it and run maintained randomized-delay/failure load plus real MCP/extension adapters, confirming bounded actual overlap and responsive transient activity.

**Verifies:** tool contracts/policy and dynamic capability sources, governed execution and in-process concurrency, hooks and typed tool behavior, overlapping activity timing, deterministic canonical tool continuations, session safe boundaries, and host-owned effect metadata, conflict graph, bounded true concurrency, and deterministic structured join.


---

## Scenario X — Governed Search-Result and Direct-URL Fetch

1. Inspect an ordinary conversation turn before web research. Confirm `web_fetch` is absent from the model-visible tool schemas and canonical inventory while remaining registered internally.
2. Enable `web_search` with retrieval-aware repository consent, run a deterministic search, and confirm each normalized HTTPS result receives a bounded opaque reference tied to repository, session, invocation, result ordinal, URL digest, consent/policy generation, and expiry without exposing authority-bearing metadata.
3. Continue the same governed turn. Confirm the host progressively activates one `web_fetch` schema only while retrieval-aware outbound consent, effective fetch policy, and eligible result evidence all remain valid; record the intentional canonical tool-generation change and accept a selected opaque result ID without requiring the model to reproduce its URL. Revoke consent or disable fetch and confirm neither result evidence nor a direct grant can retain or reactivate the schema.
4. Fetch an allowlisted public HTML fixture through a deterministic transport. Confirm URL/DNS/public-address checks, connection-time destination pinning with TLS hostname validation, fixed credential-free headers, manual redirects, streamed bounds, and deterministic readable extraction.
5. Inspect the result. Confirm sanitized query-free requested/final provenance URLs, non-reversible exact-URL digests where correlation is required, retrieval time, declared/effective media type, title, decoded/extracted digests, extractor version, byte counts, sanitized redirect summary, and exact truncation provenance accompany bounded text framed only as untrusted external evidence. Confirm raw HTML/headers and exact/query-bearing transport URLs are neither model-visible nor durable and do not enter logs, telemetry, diagnostics, events, or errors.
6. Expire, alter, replay, or move the opaque reference across session/repository/consent generations. Confirm rejection before DNS/network activity and removal of progressive activation when no eligible result remains.
7. Supply a direct absolute HTTPS URL. Confirm exact one-shot user authorization is required outside repository control; deny it and observe zero network traffic, then grant it and confirm only that canonical URL/invocation is authorized. Repository configuration, model text, fetched instructions, hooks, extensions, and trust cannot grant or broaden it.
8. Restore a legacy legacy search-only consent record. Confirm search may retain its compatible behavior but fetch requires visible re-consent; no migration silently broadens outbound disclosure.
9. Exercise loopback, private, link-local, ULA, CGNAT, multicast, unspecified, reserved, cloud-metadata, IPv4-mapped unsafe IPv6, mixed DNS, and DNS-rebinding fixtures. Confirm no unsafe socket connection occurs even when preflight and connection-time answers differ.
10. Exercise HTTPS downgrade, cross-origin, looped, excessive, relative, malformed, and unsafe redirect chains. Confirm automatic redirects are disabled and every accepted hop independently passes authorization, DNS/address, connection, timeout, and bounds. For direct flow, confirm same-origin and cross-origin redirects stop before DNS unless every canonical target has its own exact invocation-bound authorization.
11. Return gzip/brotli bombs, oversized compressed/decoded/extracted content, extreme HTML/XML nesting and node/attribute counts, deeply nested or high-token-count JSON, oversized parser strings, stalled headers/body/parser work, retryable failures, invalid encodings, missing/conflicting/mislabeled media types, binaries, archives, PDFs, and active HTML/entity/subresource constructs. Confirm parser-specific limits apply during tokenization/construction, cooperative cancellation and total deadlines interrupt work, any admitted isolated non-cooperative parser is forcibly terminated within a bounded backstop, independent byte/output limits hold, no active/resource execution occurs, and failures remain sanitized and bounded.
12. Attempt to inject system instructions, authorize another URL, activate tools, approve mutations, or disclose secrets from fetched text. Confirm content remains quoted untrusted evidence and has no authority; following another link requires a new governed invocation/authorization.
13. Run eligible fetches under host scheduling and rate limits with randomized completion. Confirm only host-proven origins overlap, same-origin/source caps hold, activity durations are truthful, and model continuations remain original-order canonical.
14. Revoke consent, disable tools, change policy/repository/session, cancel, and shut down during DNS/connect/body/extraction. Confirm safe bounded termination, generation invalidation, no leaked transport/activity, and no false safe boundary.
15. Inspect `/tools`, context inspection, headless output, telemetry, and diagnostics. Confirm dormant/eligible/direct-authorization states and sanitized size/timing/outcome provenance without full query URLs, DNS answers, opaque IDs, headers, bodies, content, cookies, or credentials.
16. Repeat with an explicitly opted-in maintained public documentation site. Confirm useful readable content and provenance while treating Internet availability/content as non-deterministic and making no browser/authenticated-download claim.

**Verifies:** centralized tool policy/availability and persistence/hardening, provider and governed search consent/provenance, managed policy hooks and activity, canonical progressive tool generations and deterministic context/continuations, session safe boundaries and scheduling, and opaque-result eligibility, direct authorization, SSRF/DNS-rebinding-safe transport, bounded readable extraction, and untrusted external-evidence contract.


---

## Scenario Y — Interactive and Headless MCP Lifecycle Management

1. Configure trusted machine/user profiles covering stdio, SSE, streamable HTTP, static-token HTTP, and OAuth, with a mixture of `autoConnect` values and allowed tool/resource/prompt capability kinds. Add repository-owned/self-trusting and invalid profiles as denied fixtures.
2. Launch Threadsmith and run `/mcp list` plus the headless equivalent. Confirm every effective profile appears—including disconnected non-auto-connect profiles—with bounded source eligibility, transport, trust, auth mode/state, lifecycle state, capability counts, enabled-tool count, and sanitized timing/error data. Confirm no command arguments, environment, headers, tokens, claims, endpoint query/path, or server content appears.
3. Select a non-auto-connect stdio profile through numbered choices and connect it. Confirm one host manager validates current profile/source/trust/secrets/executable/policy, serializes the transition, reports truthful startup/handshake/discovery durations, publishes capabilities atomically, and returns an idempotent outcome for a duplicate connect.
4. Concurrently request connect/disconnect/reconnect, cancel during handshake, fail partial discovery/registry publication, and transition session/repository during activity. Confirm per-profile serialization, generation fencing, safe-boundary coordination, complete cleanup, and no orphan process/client/tool entry.
5. Run `/mcp capabilities`, capability detail, and headless projections against a fixture advertising tools, resources/templates, prompts, duplicate/invalid/oversized metadata, and list-change notifications. Confirm complete sanitized descriptors within the strict connection bound, debounced list-change replacement, policy filtering, stale-generation rejection, and no insertion of the catalog into ordinary model context/tool schemas.
6. Disable one imported tool with `/mcp disable`, then inspect the ordinary model request and invoke by ID. Confirm repository tool state is the sole availability authority, the schema is absent and invocation denied, while capability inspection remains available. Re-enable it, then change the server schema identity and reconnect; confirm stale enablement fails closed pending review.
7. Read an exact discovered text resource and render a prompt with bounded arguments. Confirm MIME/schema/output/time limits, provenance, cancellation, and untrusted-evidence rendering. Attempt binary/oversized/unknown/template-escape content and prompt injection; confirm rejection or bounded metadata only, no instruction authority, and no automatic model-tool/context admission.
8. Authenticate explicit-client and URL-only OAuth profiles explicitly. Confirm list/inspect/diagnose never launch the browser, while `/mcp auth` preserves protected-resource discovery, dynamic client registration when no `clientId` is configured, configured scope caps, PKCE/state/issuer checks, callback-before-browser ordering, headless pasted-callback parity, and exact profile token/registration namespace.
9. Run local logout. Confirm the profile disconnects/drains first, only its local token namespace is atomically cleared, generations invalidate, no remote-revocation claim is made, and another profile's identity is unchanged.
10. Exercise an advertised revocation endpoint with success, unsupported, invalid-token, timeout, and ambiguous failure. Confirm truthful remote/local outcomes, explicit choice before local-only cleanup after an unconfirmed remote failure, and no token/callback/account leakage.
11. Run switch-account and cancel/fail at each boundary. Confirm it replaces the one profile identity through logout/re-auth, retains no second account, never mixes old/new credentials, and reconnects only after the fresh identity is complete. Static-token profiles reject logout/revoke/switch without deleting external secrets.
12. Run `/mcp diagnose` for configuration/source/trust, executable/network, secret-reference presence, OAuth state, handshake, discovery, capability translation, registry collision, timeout, and drain/kill failures. Confirm structured actionable classifications and monotonic phase/recent latency measurements without log scraping or arbitrary tool invocation.
13. Hang the stdio server with in-flight work, then disconnect/reconnect/shut down. Confirm draining is visible, cancellation propagates, the process tree is killed within the existing bound when necessary, registry capabilities disappear, and final state/outcome is honest.
14. Apply managed hook denials and attempt self-authorization from repository configuration, MCP descriptions/prompts/resources, model text, extensions, and untrusted hooks. Confirm none can grant profile trust, connect/authenticate, enable tools, expand secret scope, revoke identity, or bypass lifecycle policy.
15. Resume and clone a session, then restart Threadsmith. Confirm live transports/process IDs/capability handles/auth flows are never restored as authority; current trusted auto-connect or explicit connect revalidates everything, while the user token cache remains separately profile-bound.
16. Compare TUI and headless list/inspect/connect/disconnect/reconnect/capability/auth/logout/revoke/switch/diagnose outcomes, confirmation requirements, cancellation, headless-JSON/interactive-text identity, and exit codes. Confirm both call the same manager and no TUI/CLI SDK or token-store access exists.
17. Inspect events, logs, activity, diagnostics, and support bundles. Confirm bounded profile/source/outcome/duration/capability data and honest latency labels without tokens, dynamically registered client secrets, configured secrets, headers, environment, arguments, callback URLs/codes, claims/account identifiers, raw resource/prompt content, stderr, or unsafe schemas.
18. Run maintained real stdio lifecycle coverage and explicitly opted-in live HTTP/OAuth coverage. Confirm connect, inspect, latency, logout/re-auth, reconnect, and clean shutdown while retaining all transport, OAuth, tool-availability, activity, context, session, and scheduling regressions.

**Verifies:** centralized tool policy and MCP adapter/persistence/hardening, real SDK stdio/HTTP/SSE transports and OAuth, repository tool availability and context inspection, managed hooks and activity timing, canonical tool identity, session safe boundaries and scheduling, provider-neutral static-secret discovery, and one-authority profile/capability/authentication/diagnostic lifecycle with interactive/headless parity.


---

## Scenario Z — Deterministic Introduced-change Review and CI Gate

1. Create a deterministic Git fixture with a base branch and head containing modified, added, deleted, renamed, generated, binary, and submodule paths plus an unrelated pre-existing defect. Start `/review` for the local range and run the equivalent headless command.
2. Confirm the host resolves exact repository/base/head/merge-base objects, freezes one canonical rename-aware patch with blob/hunk/line/symbol identities, and records generated/binary/submodule/degraded coverage before any reviewer runs. Move/force-update a ref during capture and confirm fail-closed stale-source behavior.
3. Repeat with working-tree and existing approved execution/delegated-worker change-set sources. Confirm staged/unstaged/untracked eligible bytes are content-addressed without staging or mutating Git and that every mode produces explicit source provenance.
4. Supply external PR metadata through a deterministic extension/MCP fixture. Confirm repository and exact commits/provider version are matched to the active repository; model text, repository content, title/body, adapter prose, and environment variables cannot redefine source authority. Missing shallow history requires an explicitly authorized typed recovery or fails without implicit network.
5. Have delegated-worker, maintained, and domain reviewers inspect security, tests, performance, and architecture. Confirm one immutable snapshot, narrow role-specific context/tools/skills/models/budgets, progressive disclosure only for selected categories, deterministic join, and no reviewer publication, waiver, resolution, gating, delegation, or mutation authority.
6. Return valid findings on introduced lines, a deletion consequence, an unchanged sink causally affected by introduced behavior, and the unrelated pre-existing defect. Confirm exact head/base/contextual mapping, introduced-behavior eligibility, summary fallback where no valid inline head location exists, and exclusion of the unrelated defect from gate failure.
7. Return malformed, uncited, out-of-range, duplicate, near-duplicate, conflicting-severity/confidence, and unsupported-consequence findings. Confirm schema/citation/location/consequence rejection, stable fingerprints, exact deduplication, conservative clustering, and preserved reviewer disagreement/provenance.
8. Rerun after fixing, renaming, moving, or reintroducing implicated code. Confirm open/acknowledged/fixed/stale/recurrent correlation uses immutable lineage/path/symbol/rule/evidence identity; uncorrelatable findings never silently become fixed and reviewers cannot change dispositions.
9. Apply exact expiring user and trusted managed-policy waivers, then attempt waivers from repository configuration, model/reviewer text, PR content, extensions, and untrusted hooks. Confirm only authorized actors can disposition, scope/expiry/justification are audited, and hard non-waivable controls remain dominant.
10. Exercise severity, confidence, category, required-reviewer, coverage, omission, and publication thresholds. Confirm deterministic `Pass`, `PassWithNotes`, `FailFindings`, `FailCoverage`, `FailInfrastructure`, `Cancelled`, and `InvalidSource` outcomes plus versioned headless exit classes independent of model prose.
11. Export canonical JSON, SARIF 2.1.0, CI annotations, and TUI/console summaries. Validate schema/rules/regions/fingerprints/related locations/suppressions/invocation outcome, deterministic ordering/digests, repository-relative safe paths, bounded text, and agreement with one canonical gate result.
12. Preview and publish selected exact-line comments/check summary through the provider fixture. Confirm only open non-duplicate threshold-eligible findings with valid provider locations publish; deleted/contextual findings use summary form; exact head/version, enabled capability, secret scope, policy, and idempotency are rechecked.
13. Inject timeout, stale head, partial publication, lost response, retry, and provider duplicate behavior. Confirm reconciliation never duplicates comments, overstates a completed check, or publishes against a changed head, and core grants no commit/push/approval/merge authority.
14. Run noninteractive external-fork CI with no privileged secrets. Confirm explicit inputs, no prompt fallback, bounded detached/ephemeral workspace, no Git mutation/hooks/executable filters, process/network policy preservation, atomic artifacts, cancellation, cleanup, and no privileged publication.
15. Cancel or crash after every durable boundary: source freeze, reviewer selection/terminal, finding join, dispositions, gate compile, exports, publication intent/result, and terminal outcome. Confirm resume revalidates source/provider/policy/tool/skill/model generations and produces no duplicate reviewer, finding, waiver, artifact, comment, or check effect.
16. Select findings for remediation. Confirm Threadsmith starts ordinary governed planning/execution with exact diff/approval/validation and that only a fresh review rerun—not implementation claims—marks findings fixed or changes the gate.
17. Add managed hooks requiring a domain reviewer, stricter threshold, and publication block. Confirm trusted policy can narrow/block and contribute validated advisory findings but cannot rewrite commits/diff/citations, weaken hard gates, self-waive, or publish directly.
18. Inspect events, logs, persistence, diagnostics, and bundles. Confirm bounded IDs/counts/digests/roles/models/coverage/gate/publication outcomes and durations without secrets, provider tokens, private PR bodies, raw diffs, hidden reasoning, reviewer transcripts, or unbounded payloads.
19. Run maintained real local-Git review and explicit-opt-in provider retrieval/publication fixtures. Confirm local review has no provider dependency and all delegation/skill, Git/semantic/test, hook, canonical-context, session/scheduling, MCP, persistence, redaction, and architecture regressions remain green.

**Verifies:** workspace/diff/validation and execution foundations, parallel reviewers and governed skills, managed organization policy, rich Git/semantic/test evidence, operational/activity/context/session/scheduling safety, optional managed MCP lifecycle/publication support, and authoritative source, introduced-change correlation, finding lifecycle, deterministic gate, standardized outputs, ephemeral CI, and idempotent provider publication.


---

## Scenario AA — Low-friction User and Inline Web Fetch Authorization

1. Start an ordinary turn with no search evidence, direct grant, or URL in the current user message. Confirm `web_fetch` remains absent from the model schema and canonical inventory.
2. Submit `Read https://public.example/package/docs` after accepting the revised disclosure. Confirm the host recognizes the exact URL from fresh raw user-input provenance, issues an opaque message/repository/session/run/generation/expiry-bound reference, activates one fetch schema, and performs no DNS/network I/O merely from recognition.
3. Have the model invoke `web_fetch` with that opaque user URL reference. Confirm it consumes once, traverses every legacy web-fetch consent/policy/URL/DNS/address/connection/redirect/transport/content/provenance gate, and returns bounded untrusted evidence without requiring `/fetch-authorize`.
4. Replay the reference; advance to another top-level user turn; complete/cancel the run; revoke consent; disable fetch; change policy/options; run `/open`, `/new`, `/resume`, and `/clone`; then restart. Confirm authority is revoked, never reconstructed from archived conversation, and produces zero unauthorized network activity.
5. Supply duplicate bare/Markdown URLs, terminal punctuation, malformed URLs, credentials, HTTP, non-default ports, overlong input, and more candidates than the cap. Confirm deterministic recognition/deduplication, bounded errors or omission, no authority for invalid/excess candidates, and no URL/token/query leakage.
6. Place URLs in prior/restored messages, governed memory, repository files, prompt appends, search snippets lacking references, fetched content, model output, tool results, extensions, MCP, and hooks. Confirm none can mint current-user references or activate fetch through that route.
7. Restore legacy web-fetch consent schema 2 and attempt the current-message route. Confirm visible re-consent is required and denial performs no fetch; existing compatible search-result and explicit-command behavior is not silently reclassified.
8. While fetch is legitimately active, have the model propose a different structurally valid direct HTTPS URL. Confirm the pipeline stops before DNS/network and the interactive host shows one bounded approval identifying model provenance, origin/path, query presence or safe key metadata, exact digest, one-shot scope, no ambient credentials, and no redirect authority without printing query values.
9. Deny, cancel, time out, and close the approval surface. Confirm a stable bounded denial, responsive return to the composer, no stale grant/activity, and no model/repository/content/hook/extension/MCP or trust/mutation-policy path that can approve or remember it.
10. Approve one proposal, then race/replay it from another sibling, retry, invocation, redirect, session, and run. Confirm only the original pending invocation receives one exact grant, approval prompts serialize in canonical order, no prompts overlap, and sibling-tool execution cannot deadlock or reorder continuation results.
11. Exercise the same ungranted model proposal headlessly. Confirm no prompt or opportunistic stdin read occurs, a stable sanitized `DirectAuthorizationRequired`-class outcome/exit code is returned, and an explicit headless grant succeeds through the same governed fetch path.
12. Pre-authorize an initial URL plus redirects through `/fetch-authorize` and its headless equivalent. Confirm the exact group still works, while current-message and inline single-URL authority stop before DNS on an unapproved redirect and never become origin/session allowlists.
13. Inspect `/tools`, context inspection, events, activity, telemetry, diagnostics, support bundles, and restored state. Confirm bounded activation source/count/approval outcome/timing/digests without exact protected URLs, query values, opaque IDs, raw model arguments, headers, bodies, or live grants.
14. Repeat the natural-language exact-URL and model-proposed approval flows in a maintained real terminal against an explicitly opted-in public documentation site. Confirm the common path is materially easier than command pre-authorization while preserving native paste/selection, cancellation, truthful activity, and every governed fetch network/content boundary.

**Verifies:** user intake and TUI authority, centralized tool/persistence/hardening/availability, conversation provenance and governed search, managed policy/activity/canonical context/session/scheduling, governed retrieval, and current-user URL references plus exact inline/headless authorization behavior.


---

## Scenario AB — Extensible Secret Discovery

1. Configure the same canary logical reference in the user store, an effectively ignored repository store, and the matching `THREADSMITH_` environment variable. Resolve it from a typed secret-aware model field and from an explicit component request. Confirm both use the same resolver and deterministic environment → repository → user precedence without projecting the value.
2. Remove each higher-priority source in turn. Confirm fallback is deterministic, reports only safe source metadata, and permits repository values only when the request accepts `RepositoryOwned` trust.
3. Put the repository store under `<repo>/.threadsmith/secrets/config.json` while the exact file is tracked, staged for addition, covered only by an ignore rule evaluated with `--no-index`, not covered by an effective ignore rule, covered by a negating rule, outside a Git worktree, and with Git unavailable. Confirm every unsafe or indeterminate case rejects before reading values with exact safe remediation. Remove all applicable index entries, add an effective ignore rule, and confirm the now-untracked ignored store resolves.
4. Attempt traversal, symlink/reparse escape, malformed/duplicate/oversized/deep JSON, invalid UTF-8, unsafe user-file permissions, empty values, and canonical-name collisions. Confirm fail-closed bounded classifications with no content or value leakage.
5. Place `secrets:example` in an ordinary untyped string, repository text, prompt append, model/tool/MCP/extension/hook output, context inspection, and diagnostic input. Confirm none triggers lookup. Mark the precise typed field secret-aware and confirm resolution occurs only at its final privileged operation boundary.
6. Exercise migrated model-provider, Brave search, MCP static-auth/bootstrap, hook, NuGet/private-source, and other static credentials through each eligible provider. Confirm consumers do not select providers or read configuration directly and existing environment names remain compatible.
7. Exercise MCP and Codex OAuth login, refresh, logout, restoration, and failure. Confirm lifecycle-owned token caches remain compatible, static bootstrap references use the common resolver where applicable, and no OAuth token enters the generic JSON stores accidentally.
8. Trigger missing reference, no eligible provider, not found, unavailable, access denied, malformed store, timeout, cancellation, bootstrap cycle, and minimum-trust/policy rejection. Confirm interactive and headless errors identify the reference/component, safe attempted/skipped providers, stable reason, and actionable remediation without raw exceptions or values.
9. Attempt to register/reorder a provider or lower source trust from repository configuration, a skill, extension, MCP server, hook, prompt, or model call. Confirm host-owned composition rejects it and no repository secret can bootstrap user/managed authority.
10. Run concurrent resolution, user-store atomic update/removal, repository/session transitions, cancellation, and restart. Confirm no partial reads, stale repository binding, precedence drift, value leakage, or corrupted store.
11. Inspect logs, events, telemetry, persistence, hooks, context/model requests, terminal output, diagnostics, crash/startup errors, and support bundles. Confirm the canary value is absent while bounded provider/source/outcome/duration diagnostics remain useful.
12. Implement a deterministic fake future provider using only `ISecretProvider`. Confirm it can resolve an isolated namespace without modifying consumers, while duplicate IDs/priorities, unsafe bootstrap dependencies, raw provider failures, and unauthorized network/provider SDK leakage fail architecture and integration gates.

**Verifies:** configuration and host authority, provider/tool and persistence/redaction foundations, OAuth and tool availability, compiled provider/static-secret consumers, governed extension/skill/hook boundaries, packaging/user paths, existing web/MCP contracts, and provider-neutral resolver, typed secret-aware fields, repository/user/environment providers, source trust, tracked/staged index rejection, effective Git-ignore proof, sanitized failures, and future-provider extensibility.


---

## Scenario AC — Semantic Markdown Console Rendering

1. Launch an interactive styling-capable terminal with no `tui:renderMarkdown` setting and stream one Markdown-rich response across adversarial `ModelOutputObserved` chunks. Confirm `THINKING` remains truthful while accumulating, then one complete semantic document renders before composer return with headings, emphasis, ordered/unordered/task lists, a table, blockquote, inline code, fenced C# code, strikethrough, and a link—without a raw duplicate or scrollback rewrite.
2. Inspect the terminal-neutral transcript, persisted/archive representation, assembled context, and headless output. Confirm every raw chunk appended immediately in exact event order and exact Markdown source remains authoritative; no parsed AST, layout, ANSI, Markdig, PrettyPrompt, or Spectre type/value crosses the TUI boundary.
3. Set `tui:renderMarkdown: false`, restart, and repeat the same deterministic safe-text chunk script. Confirm the existing visible chunk-by-chunk cadence, text, activity transition, tool ordering, native selection, and composer return are unchanged. Then stream ANSI/OSC/C0/C1 controls and malformed Unicode; confirm exact bytes remain upstream while the interactive source path displays deterministic visible escapes and executes no control.
4. Run `answer A1` → visible host status → `answer A2` → diagnostic → `answer A3` → tool start/completion → `answer B` with adversarial chunk boundaries. Confirm each active answer closes and its write completes before the triggering status, diagnostic, or tool output; B is a later document, and no text moves, joins across a boundary, disappears, or appears twice.
5. Mix reasoning, leading whitespace-only chunks, host status, diagnostics, diffs, session transitions, prompts, tool/MCP results, unknown non-model events, and ordinary answer text. Confirm only accepted non-reasoning model answer blocks enter Markdown parsing; proven invisible non-boundary events may leave a block open, every ordered event that changes visible projection state and every declared boundary flushes first, and unknown non-model events close conservatively while retaining existing semantic paths/provenance markers. Drive several timer-only in-place refreshes of the already active activity row and confirm they do not classify an event, close, parse, or fragment the answer block.
6. Exercise every allowed syntax node plus raw HTML, images/media, unsafe URL schemes, malformed links/Unicode, ANSI/OSC/control characters, extreme nesting/emphasis, oversized lists/tables/code/links, and source beyond the configured bound. Confirm the closed profile renders supported syntax, treats unsupported content inertly or falls back deterministically, validates links, performs no fetch/execution, emits no controls, and switches an oversized active block to terminal-safe streamed source exactly once.
7. Run styled, `NO_COLOR`, `TERM=dumb`, limited-color, redirected, and headless cases, including source controls. Confirm interactive style suppression retains identical semantic words, markers, table degradation, indentation, and line layout without literal colors or executable controls; capability-proven redirected/headless output remains exact raw Markdown, and a redirected raw-source item is rejected if routed to an interactive backend.
8. Under fake monotonic time with default `tui:showOperationDurations`, run model → built-in tool → model and model → MCP → model sequences while Markdown blocks close immediately before each invocation. Advance through multiple 250 ms `THINKING` refreshes during each accumulating answer and confirm they redraw the same activity without producing intermediate documents. Confirm `THINKING` retains the original total-turn start while buffering and stops immediately before the document write; `TOOLS`/`MCP` use their original activity timing metadata after that flush, catch up rather than restart at zero, and retain bounded live updates; continuation `THINKING` resumes at the original total-turn elapsed value. Confirm completion rows retain source, name, outcome, authoritative final duration, semantic role, and single-row MCP behavior.
9. Run reviewed built-ins whose start events contain a `read_file` requested path/line range and a sanitized bounded `run_process` command. Confirm the same details appear in live and completion rows, are neither Markdig-parsed nor reconstructed/dropped/double-escaped, and raw tool/extension/MCP arguments remain hidden. Repeat with legacy missing source/duration and confirm no value is fabricated.
10. Set `tui:showOperationDurations=false`, exercise user/repository precedence and restart, and repeat Markdown/tool/MCP continuations. Confirm duration suffixes and periodic refresh stop while `THINKING`/`TOOLS`/`MCP`, source, outcome, and sanitized detail remain unchanged. After final Markdown output, confirm the complete session-status field set and its width, priority/omission, semantic-role, styling-suppression, and redirected-output behavior remain unchanged before the composer.
11. Resize before rendering across wide and narrow windows. Confirm Unicode-cell-aware wrapping, hanging list indentation, exact code whitespace, stable quote/rule markers, and deterministic table-to-labeled-row degradation; already written native scrollback is never rewritten.
12. Inject parser, semantic-validation, layout, and adapter failures with controls in the source; cancel/fail during collection, before block closure, while waiting for the console gate, during elapsed activity refresh, and during shutdown. Confirm accepted exact text remains upstream, terminal-safe source is displayed exactly once where fallback is needed, no raw control reaches `IConsoleSurface`, activity/details/final markers are not lost, timer tasks and the gate are observed/released, and run/composer state is honest.
13. Concurrently schedule bounded `THINKING` refresh, block closure, sanitized tool detail, `TOOLS`/`MCP` replacement, authoritative completion duration, semantic-document output, session status, and `RunCompleted`. Confirm one serialized console order, no overlapping terminal writes or deadlock/cursor artifact, no block flush caused solely by a refresh callback, each projection retains its content/role, and completion/status/composer are not signaled before the final write.
14. Resume a session containing raw Markdown and legacy/current tool events. Confirm historical raw transcript is not implicitly reparsed, newly observed answer blocks use the configured mode, duration/source/detail restoration retains operation-duration behavior, and source/headless behavior remains deterministic across restart.
15. Run package, architecture, session-status, and existing operation-duration and semantic-answer focused regression suites. Preserve their existing timing/detail/configuration/status assertions; add source-mode and rendered-mode branches only for final-visible-answer activity termination. Confirm the centrally pinned Markdig version/license/dependency footprint is approved for all release RIDs, Markdig is TUI-only, the host semantic document contains no parser/backend/activity/status types, and the console backend can be replaced behind `IConsoleSurface` without changing collection, timing, activity-detail, or status contracts.
16. Repeat default rendered mode and explicit source mode in maintained Windows Terminal plus Linux/macOS/SSH/multiplexer coverage using system/light/dark/custom themes, elapsed timing enabled/disabled, reviewed activity details, session status, native selection, `Ctrl+C`, 10 KB/100 KB paste, resize, long responses, tool continuations, links, tables, code, and cancellation. Confirm no activity/status/detail regression, alternate screen, mouse capture, cursor replacement, duplicate answer, or syntax-highlight requirement.

**Verifies:** conversation-first host projection, semantic themes/native-scrollback/session-status contracts, monotonic request/tool/MCP durations, bounded refresh, source/outcome/detail markers, configuration and transient activity lifecycle, and direct Markdig adapter, closed host-owned semantic document, default complete-block presentation, terminal-safe streamed source/fallback, opaque non-model projection compatibility, flush-before-visible-event-boundary ordering with periodic in-place activity refreshes explicitly non-boundary, raw transcript authority, safe content policy, and deterministic fallback.


---

## Scenario AD — Command Middleware Telemetry Activation

1. Start the production-composed host through both TUI and headless adapters and dispatch representative query, immediate command, wait command, and management command shapes. Confirm each known dispatch creates one stable command activity and one terminal structured record containing only compiled command type, `Success`, and non-negative monotonic duration.
2. Dispatch a pre-cancelled known command and a handler-cancelled command. Confirm both record `Cancelled`, preserve the original `OperationCanceledException`/token behavior, invoke the handler zero or one time as appropriate, and never translate cancellation into success or failure.
3. Dispatch a command whose handler throws a canary exception containing secret text. Confirm one `Failure` outcome and activity error status, the exact exception instance and original stack propagate, and the message, exception object/`ToString()`, command properties, response values, and canary are absent from logs, activity tags/events, diagnostics, and support-bundle inputs.
4. Register two recording test middleware around the telemetry stage. Confirm deterministic registration-order nesting, `next` executes exactly once, the exact successful response is preserved, and null/unknown-command setup failures retain their documented fail-fast behavior.
5. Submit a request that returns a `RunId` before detached work later succeeds, fails, or is cancelled; separately exercise execution preparation and uncertain mutation reconciliation. Confirm dispatcher duration ends with the handler task, background/operation telemetry remains separately truthful, and execution-owned catches still perform durable transitions, diagnostics, rollback/reconciliation, and completion signaling.
6. Exercise existing logger and activity filtering, then vary repository configuration across supported formats and precedence. Confirm filtering controls emission/collection without removing the middleware, while repository configuration cannot enable, disable, enrich, retag, or add payload fields. Run focused telemetry/dispatcher/composition/TUI/headless tests, architecture tests, the solution build, formatting checks, and canary-redaction verification; confirm no new configuration, public DTO, durable event, policy authority, user-visible output, or dependency-direction violation.

**Verifies:** the shared command adapter and middleware ordering/cancellation contract, redaction and diagnostic safety, operation-timing distinction, production composition parity, and AR-01 metadata-only command telemetry activation with unchanged command semantics.


---

## Scenario AE — Cohesive Application Composition Boundaries

1. Inventory the production graph before refactoring and start it afterward through both TUI and headless adapters. Confirm the same command handlers and the exact same singleton authority instances back MCP lifecycle, active model selection, tools and repository availability, repository binding, sessions, execution, skills, and agents.
2. Inspect the new application-composition inputs. Confirm the flat 31-property `ApplicationCompositionContext` is absent; each immutable internal input or narrow factory has one documented subsystem responsibility, no miscellaneous/untyped lookup, and no passive disposal ownership.
3. Exercise successful startup and shutdown while recording resource identity and disposal order. Confirm already-initialized dependencies are borrowed by composition inputs, every owned resource is disposed exactly once in reverse dependency order, and shared HTTP/event/model/MCP resources outlive all consumers that require them.
4. Inject failures after representative foundation, model, MCP, and application construction boundaries and cancel cancellable startup work. Confirm previously created owners clean up their resources, borrowed inputs neither dispose nor recreate dependencies, cancellation propagates unchanged, and no constructed-but-uninitialized application surface escapes.
5. Exercise command success, cancellation, and failure through the command middleware, repository/model changes, MCP management, and a background request. Confirm one production dispatcher and middleware instance remain, authority identity and lifecycle behavior are unchanged, and TUI/headless output, public APIs, schemas, configuration, persistence, and telemetry fields have no behavioral delta.
6. Run focused bootstrap/composition/command-middleware tests, architecture tests, the solution build, formatting checks, and static guards. Confirm no DI container, Scrutor/assembly scanning, `IServiceProvider`, service locator, property injection, new cross-layer dependency, or public composition contract was introduced.

**Verifies:** single MCP lifecycle authority, production command-middleware composition, and cohesive non-owning application inputs with unchanged manual async construction, trust visibility, authority identity, disposal, cancellation, and external behavior.


---

## Scenario AF — Extension Generation Read-Only Capability Views

1. Load a test extension that registers one tool capability and one model-preference contributor. Capture `generation.Tools` and `generation.ModelPreferenceContributors` before and after activation registration. Confirm repeated access returns the same wrapper objects and the captured live views expose the registered items in original order.
2. Attempt to cast each public value to its concrete backing `List<T>`. Confirm the cast cannot recover the backing list. Cast each value to `ICollection<T>` and attempt `Add`, `Remove`, and `Clear`; confirm every mutation is rejected with `NotSupportedException` and generation/registry state is unchanged.
3. Activate and invoke the test tool through the normal capability registry. Confirm capability identity, generation fencing, invocation budgets, leases, model-preference aggregation, and registry publication behave exactly as before.
4. Unload the generation while retaining references to the previously captured public views. Confirm both views become empty after `ClearCapabilities()`, no copied snapshot retains extension-defined capability objects, drain/removal ordering remains unchanged, and collectible-load-context verification succeeds.
5. Exercise failed activation and hot replacement. Confirm failed generations publish no capabilities, predecessor/successor ownership remains exact, clearing an old generation cannot remove or retain its successor, and unload-blocker reporting has no behavioral delta.
6. Run focused extension tests, public-API and architecture tests, the solution build, formatting checks, and `git diff --check`. Confirm public property signatures, dependency direction, telemetry, configuration, durable state, and user-visible extension behavior remain unchanged.

**Verifies:** extension activation, capability registration, generation fencing, hot replacement, and collectible unload contracts plus cached live read-only views that close the public backing-list mutation path.


---

## Scenario AG — Secondary HTTP Connection Lifetimes

1. Inventory every production `HttpClient` construction site and classify it as externally injected, one-shot, request-scoped/pinned, or host-owned long-lived. Confirm the inventory includes primary models, hooks, Brave search, MCP HTTP transports, MCP identity/revocation, authentication helpers, and WebFetch.
2. Inspect each internally created long-lived secondary pooled handler. Confirm hooks, Brave search, MCP HTTP transport, and MCP identity/revocation have a finite positive `PooledConnectionLifetime`, while their existing redirect, decompression, connect/request timeout, credential, proxy/cookie, and disposal behavior remains unchanged.
3. Inject caller-owned clients into MCP transport and identity components. Confirm the exact clients are used without handler replacement or disposal by the component.
4. Exercise primary model requests and short-lived authentication operations. Confirm the primary configured pooling contract and natural one-shot lifetime remain unchanged.
5. Exercise governed WebFetch across direct, redirect, DNS-rebinding, and cancellation cases. Confirm it still creates request-scoped handlers, validates and pins current public addresses, closes connections, sends no ambient authority, and never adopts general pooled reuse.
6. Run hook, web-search, MCP, model, WebFetch-security, architecture, build, formatting, and `git diff --check` gates. Confirm no DI HTTP factory, public/configuration contract, repository-controlled network widening, or telemetry disclosure was introduced.

**Verifies:** operationally bounded DNS refresh for host-owned long-lived secondary HTTP clients while preserving WebFetch connection-time SSRF defenses, injected-client ownership, and existing protocol/security behavior.


---

## Scenario AH — Profile-Guided Text Allocation Reduction

1. Run a warm repeated allocation harness over small, typical, and bounded-large OpenAI-compatible and Codex SSE streams, sanitizer inputs, and semantic Markdown documents. Record bytes/allocations per operation, throughput variance, input shapes, runtime, and environment using synthetic non-secret content.
2. Before production edits, declare a materiality threshold from observed variance. Separate required durable output strings from removable intermediate allocations and reject candidates whose downstream APIs recreate equivalent strings.
3. For each qualifying candidate, apply one local span/slice change and rerun measurements. Retain it only when allocation improvement clears the threshold with no meaningful throughput regression; otherwise revert it. If none qualifies, record the evidence-backed no-change disposition.
4. Replay fragmented, empty, malformed, cancellation, timeout, `[DONE]`, usage, reasoning, and tool-call SSE fixtures through both providers. Confirm exact chunks, errors, bounds, and cancellation behavior are unchanged.
5. Replay all secret/control canaries and adversarial sanitizer fixtures. Confirm exact redaction/control removal remains fail-closed and no secret-bearing buffer is pooled, retained, logged, or emitted.
6. Replay Markdown golden documents across widths, Unicode, long tokens, code whitespace, links, tables, lists, fallback, cancellation, and source mode. Confirm exact semantic segments, roles, cell widths, wrapping, and raw transcript authority remain unchanged. Run focused suites, architecture/build/formatting gates, and archive the concise measurements/disposition.

**Verifies:** evidence gate for allocation optimization without cosmetic Span adoption or regression to provider parsing, security sanitization, Unicode-aware terminal layout, readability, or public behavior.


---

## Scenario AI — Public Release License Closure

1. From a clean trusted checkout, restore and publish the application and isolated worker for every supported RID. Confirm the host-owned release evidence validator identifies the exact resolved package, runtime, ripgrep, and bundled native closure; unknown, stale, unapproved, or repository-configured components fail before staging.
2. Review the current owner-approved Windows self-contained-runtime decision against its exact SDK/runtime/RID scope and expiry trigger. Attempt a Windows publication with a missing, stale, or mismatched decision; confirm it fails closed without archive, installer, signing, or attachment publication.
3. Generate the human-readable third-party notices and SPDX/CycloneDX SBOM twice from identical approved inputs. Confirm byte-stable ordering/digests, full closure coverage, component/source/version parity, and inclusion of MIT, BSD-2-Clause, Apache-2.0, PrettyPrompt MPL-2.0/source-availability, and applicable SQLitePCLRaw NOTICE material. Confirm an omitted, duplicate, malformed, oversized, or unapproved legal input fails safely without network lookup or script execution.
4. For each RID, stage the exact runtime `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` selected by publish. Confirm canonical paths, version/digest binding, no reparse points or path traversal, app/worker alignment, and presence in the archive and inspectable installer payload. Confirm a substituted/missing/wrong-RID runtime legal file blocks release.
5. Confirm the existing ripgrep `LICENSE-MIT`, `UNLICENSE`, and `SOURCE.json` remain present with their pinned source/hash provenance. Confirm root Apache-2.0 licensing, generated notices/SBOM, runtime legal files, and ripgrep material coexist without duplicate/conflicting authorities.
6. Exercise archive/installer validation, aggregate checksums/provenance, immutable tag/head fencing, failed-release cleanup, and six-RID clean-environment rehearsal. Confirm a failed legal gate cannot publish GitHub attachments, expose signing/OAuth secrets, execute bundled content, or leave an apparently successful release. Review the separately scoped GitHub Action, installer, signing/notarization, Terminal.Gui, and unused-dependency dispositions.

**Verifies:** canonical self-contained payloads and ripgrep provenance, operational fail-closed diagnostics, and the approved legal closure, deterministic notice/SBOM generation, RID runtime legal staging, and artifact-first publication gate.


---

## Scenario AJ - Codex-Style Tool and Mutation-Diff Presentation

1. In an interactive TUI session with operation durations enabled, invoke representative built-in tools including file read, symbol/search, repository/Git inspection, and a failing or cancelled tool. Confirm each completed tool displays as `• TOOLS: <name> - <completed|failed|cancelled> · <elapsed>` followed by `  └ <bounded sanitized detail>`.
2. Disable operation durations through the effective TUI setting. Repeat representative tool invocations and confirm the same two-line block shape appears without the elapsed suffix.
3. Invoke MCP-imported, extension-backed, and unknown/fallback tools through test fixtures. Confirm each uses the same block grammar, closed outcome vocabulary, original host-owned/canonical ordering under parallel execution, and a concise safe detail line without raw JSON arguments, secrets, terminal controls, stack traces, or provider/MCP payloads.
4. Review a governed mutation preview with one or more unified-diff hunks. Confirm each `@@ ... @@` hunk header is followed by exactly one presentation-owned blank line before displayed code, while file headers, metadata, no-newline markers, and raw canonical diff content remain unchanged.
5. Confirm added and removed diff line styling remains unchanged and neutral/context diff code text can be configured independently through the semantic TUI role system.
6. Copy visible transcript text and inspect durable/headless outputs. Confirm native selection/copy remains usable and presentation-only hunk spacing/tool formatting does not mutate canonical tool continuations, raw diffs, mutation validation inputs, durable records, or machine-readable outputs.

**Verifies:** centralized completed-tool presentation, bounded sanitized details, duration-enabled/disabled grammar, parallel ordering preservation, presentation-only mutation-diff hunk spacing, neutral diff-code role configurability, and preservation of mutation authority, timing semantics, canonical ordering, and terminal-safe rendering boundaries.


---

## Scenario AK - Roslyn-Based Pre-Mutation Analysis

1. Start governed implementation for a C# change and have the model propose a `.cs` mutation with malformed member syntax. Confirm the host applies it only to an in-memory overlay, reports Roslyn parse diagnostics mapped to changed hunks and containing syntax where available, asks for proposal-phase repair, and shows no user approval prompt or disk mutation for the bad candidate.
2. Have the model revise the mutation to syntactically valid code that still contains a missing symbol, wrong overload, nullable violation, or interface implementation mistake in a loaded project. Confirm bounded semantic and fast compilation diagnostics run without `dotnet build`, include project/TFM/confidence/omission metadata, and trigger another proposal-phase repair when blocking.
3. Add an analyzer/code-style violation covered by trusted in-process analyzer configuration, such as missing public XML docs, async naming, static-member suggestion, StyleCop/CA rule, or nullable guardrail. Confirm available analyzer diagnostics are surfaced before approval with guardrail/category mapping, while unavailable or degraded analyzer coverage is reported honestly.
4. Confirm the correction packet given to the model contains only bounded host-owned fields: mutation/plan identifiers, file/range, diagnostic ID/severity/message, changed hunk, nearby context, containing type/member/symbol, source (`Syntax`, `Semantic`, `Compilation`, `Analyzer`, or `HostValidation`), confidence, omissions, and valid schema/argument examples for host validation failures.
5. Exhaust the configured repair-round or time budget, cancel during Roslyn analysis, and restore from every durable safe boundary. Confirm no candidate is applied, late Roslyn results from stale generations are discarded, repository state remains unchanged, and the user sees an inspectable bounded failure.
6. Produce a candidate that passes cheap gates. Confirm the normal exact diff is then presented under existing mutation approval policy, approval remains required, transactional apply writes exactly once, and authoritative build/test validation still runs and can still fail independently.
7. Exercise orphan `.cs` files, unloaded projects, generated/linked files, multi-TFM projects, source-generator-dependent diagnostics, baseline pre-existing diagnostics, and dependent-project omissions. Confirm the gate degrades explicitly rather than overstating certainty or blocking solely because optional Roslyn checks are unavailable.
8. Inspect activity, TUI/headless output, context/model continuations, events, telemetry, durable records, diagnostics, and support bundles. Confirm metrics distinguish diagnostics caught pre-build from build/test failures and record repair rounds/invalid proposals/schema mistakes without raw source contents, secrets, Roslyn objects, raw build logs, or provider payloads.

**Verifies:** in-memory pre-mutation overlay, syntax/semantic/compilation/analyzer cheap gates, bounded proposal-phase repair loop, diagnostic-to-hunk/symbol correlation, adaptive repair-phase tool surface, tool-result-aware correction feedback, candidate scoring, and preservation of approval/transaction authority and authoritative build/test validation.


---

## Scenario AL - Plan Approval Policy and Sanity Checks

1. Configure `ReviewRisky` through `/plan-policy` in a trusted disposable repository. Ask for a one-file source edit whose structured plan declares an exact existing repository-relative affected file. Confirm the host runs plan sanity checks, classifies the plan as low risk, records policy auto-approval, shows concise auto-approved status, and proceeds to mutation proposal without a manual plan prompt.
2. Repeat with `ReviewAll`. Confirm the same sanity checks run before the plan is shown, but manual approval is still required after the checks pass.
3. Have the model first propose a plan whose structured affected file does not exist. Confirm the host does not show the invalid plan to the user, returns bounded plan-revision evidence to the model, and accepts a revised plan with the correct path within budget.
4. Repeat with a bare ambiguous file name, empty `fileIntents` under a policy that requires concrete files, create target that already exists, protected/secret/Git path, generated/binary file, lifecycle delete/move, dependency/project change, and managed lifecycle policy denial. Confirm repairable issues revise, risky issues prompt under policy, and hard issues fail closed.
5. Exercise `ReviewAll`, `ReviewRisky`, `TrustSession`, `AlwaysTrustRepo`, and the strongest explicit auto-approval mode. Restart and switch repositories. Confirm every policy except `TrustSession` persists in repository settings, `TrustSession` does not rewrite repository settings, `AlwaysTrustRepo` is bound to exact repository identity, repository content cannot grant identity-fenced trust to itself, and reset/revoke restores `ReviewAll` without overwriting unrelated configuration.
6. Under controlled filesystem failure, grant `AlwaysTrustRepo` and make the repository marker write fail after the user grant succeeds. Confirm the grant is compensated and no success event or stronger in-memory policy appears. Then leave persistent trust while making the repository downgrade marker fail; confirm revocation occurs first and remains revoked. Preserve unrelated repository and user JSON in both cases.
7. Confirm every approved or auto-approved plan remains a durable structured contract: later mutation proposals must cite existing step ids, stay within approved scope, and still pass pre-mutation Roslyn screening, exact-diff mutation approval policy, transactional application, post-mutation build/test validation, correction, cancellation, and resume gates.
8. Inspect interactive/headless output, context/model continuations, events, telemetry, persistence, diagnostics, and support bundles. Confirm plan-sanity and auto-approval records include policy/risk/scope/revision/provenance but no source contents, secret values, raw hook payloads, or provider data.

**Verifies:** separate plan approval policy, `/plan-policy` command, all-plan repository sanity checks before review/auto-approval, bounded plan-revision repair, risk-to-policy mapping, repository identity fencing, preserved structured plan contracts, and preservation of mutation authority and validation gates; additionally verifies focused repository/user storage ownership and the unchanged fail-closed compensation protocol.


---

## Cross-cutting note

Scenarios B, C, J, K, L, Q, R, S, T, U, V, W, X, Y, Z, AA, AK, and AL exercise the **Execution Turn & Concurrency Contract (§10.7)** and the **Semantic Confidence Levels (§13.x)** under load. Scenario M separately verifies that hook execution preserves the same turn and authority boundaries. These scenarios explicitly assert:
- Staging is not visible to read tools mid-turn (Scenario B step 6/8 ordering).
- Introduced-vs-baseline classification is authoritative at `FullSemantic` and reports `ConfidenceDegraded` otherwise (Scenario C step 2).
- The correction loop stops at the configured budget (Scenario C step 8).

---

## Scenario AM - Repository-Scoped Cross-Session Memory

1. Open a repository and explicitly remember a repo-scoped fact. Confirm the host writes bounded structured memory with user-authored authority, repository identity, source message provenance, sensitivity metadata, and active validity into the existing ignored repository SQLite store.
2. Start a new independent session in the same repository and run memory list/inspect plus the headless equivalents. Confirm the remembered item is visible with stable JSON, provenance, validity, and no dependency on the prior session transcript.
3. Ask a relevant follow-up question or task. Confirm context assembly retrieves the active memory only when relevant and within budget, and `/context inspect` reports inclusion or omission rationale, token accounting, authority, and source identity.
4. Supersede the item with a user correction. Confirm retrieval prefers the replacement, the older item becomes superseded/rejected rather than silently deleted, and audit provenance remains inspectable.
5. Create repository-dependent memory backed by a file, symbol, project, or repository revision, then change the supporting repository state. Confirm turn-boundary invalidation marks the item stale and excludes it until validation reactivates it or keeps it stale with a bounded reason.
6. Attempt to create, authorize, or elevate memory from tracked repository files, prompt appends, skills, hooks, ordinary repository configuration, model text, and assistant claims. Confirm none can create authoritative memory without an explicit host command, host-observed event, or validated governed evidence.
7. Exercise optional model-proposed memory candidates with malformed schema, unsupported source IDs, oversized content, secret-like text, invented completed work, and stale repository claims. Confirm every unsafe candidate is rejected and the previous active memory snapshot remains valid.
8. Inspect Git status, diagnostics, logs, events, support bundles, persistence restore, `/new`, `/resume`, and process restart. Confirm `.threadsmith/threadsmith.db` remains ignored/local, raw secrets/hidden reasoning/provider payloads are absent, and repository memory survives local lifecycle operations without becoming shared/team memory.

**Verifies:** repository identity and persistence, conversation/context governance, session lifecycle, repository instruction safety, skill/hook/config trust boundaries, redaction, invalidation, and local repository-scoped memory that is structured, attributable, bounded, inspectable, and not shared through Git.

---

## Scenario AN - Packaged Local Documentation Help Skill

1. Build each published release payload and installer fixture. Confirm the curated local documentation bundle contains user, operation, authoring, architecture, testing, guardrail, license, and reference docs selected by the manifest, and confirm no `docs/implementation-plans/**` file or generated/local runtime state is present.
2. Launch Threadsmith from the packaged payload with no repository open and ask a natural product-help question, such as how to compact context. Confirm the ordinary model request does not advertise any new docs-specific tool, but may invoke the maintained docs-help skill through the existing `invoke_skill` path when phase, trust, and tool policy allow it.
3. Ask questions about commands, configuration, skills, model providers, context, hooks, extension authoring, release packaging, and troubleshooting. Confirm the skill searches/reads only the packaged local docs root, returns bounded answers citing doc paths/headings/snippets, and states uncertainty when shipped docs do not answer.
4. Open a repository containing conflicting prompt appends, `.threadsmith` configuration, repo skills, hooks, MCP content, and documentation-like files. Confirm none can replace the packaged docs root, alter the maintained docs skill, widen tool policy, or override host/user/repository-work authority.
5. Disable `invoke_skill`, lower trust below the required level, enter an ineligible phase, remove/corrupt the packaged docs bundle, and exceed docs search/read/answer budgets. Confirm the model cannot use the skill, failures are bounded and actionable, and ordinary Threadsmith operation continues without fabricated documentation answers.
6. Inspect `/skills`, `/context inspect`, tool inventory, logs, events, telemetry, diagnostics, and support bundles. Confirm maintained skill identity, docs bundle version/path, search/read counts, citation counts, omissions, and failure reasons are bounded and secret-free, with no hidden reasoning, raw provider payloads, unbounded docs excerpts, or private runtime paths.
7. Run package validation, skill workflow, context/tool-advertisement, redaction, architecture, and release-gate tests. Confirm the docs-help feature does not change mutation, approval, process, network, MCP, hook, extension, or repository trust behavior.

**Verifies:** release packaging, maintained skill integrity/invocation, canonical tool/context governance, local documentation authority, repository trust boundaries, diagnostics/redaction, and natural Threadsmith documentation Q&A without always-advertised docs-specific tools.

---

## Scenario AO - Roslyn-Backed Task-Sufficient Code Exploration

1. Open a trusted multi-project C# repository containing overloaded symbols, interface and virtual dispatch, delegates, generated/linked documents, tests, prompt templates, JSON configuration, and project resource items. Load the semantic workspace at full confidence, then repeat relevant checks with partial compilation and an unloaded project.
2. Ask about one exact type, method, and repository-relative C# path. Confirm `code_explore` resolves every material ambiguity, returns grouped line-numbered source with stable semantic identities, file/range digests, workspace generation, confidence, completeness, selection reasons, and exact continuation targets without requiring a preliminary symbol lookup or rereading the returned source.
3. Ask how several named symbols connect. Confirm the result leads with a bounded compiler-proven call path, includes the relevant declaration bodies and call-site context, branches at interface/virtual implementations, marks delegate/dynamic/runtime boundaries honestly, preserves named anchor source when later expansion is incomplete, and summarizes callers plus direct/transitive dependent projects and tests without claiming whole-program certainty.
4. Ask an ordinary natural-language architecture or behavior question without supplying symbol IDs. Confirm deterministic identifier/path discovery resolves likely anchors, structurally connected production code outranks incidental word matches, pinned paths and explicitly named symbols remain first, and every included or omitted file has an inspectable reason.
5. Repeat with ambiguous common names, overloads, large files, generated code, test-focused questions, a large repository, tight model context, traversal/time/source limits, cancellation, semantic invalidation, and repository switching. Confirm bounded deterministic results, usable source allocation, explicit omissions, and no stale generation crossing.
6. Make an overlapping follow-up query. Confirm unchanged ranges already present in current model-visible context become precise back-references while freed budget covers new source. Edit one referenced file, compact or remove the prior result, and repeat; confirm current source is emitted again rather than hidden behind a stale pointer.
7. Ask about a C# flow whose response depends on a referenced prompt template, configuration file, additional document, or project resource. Confirm associated textual artifacts are repository-confined, bounded, relationship-labeled, optionally projected with current ranges/digests, and never executed or treated as semantic C# authority.
8. Disable or make semantic exploration unavailable, lower trust, deny a path, exceed a bound, cancel during each stage, and trigger malformed or unsupported input. Confirm controlled success-shaped empty/incomplete results where recovery is expected, classified failures where authority or integrity fails, flow/branch/blast evidence outside path policy is omitted, and safe fallback to granular semantic/text tools only when the result explicitly warrants it.
9. Inspect interactive/headless output, tool inventory, context inspection, events, telemetry, persistence, cache/stateful-continuation behavior, support bundles, and restored sessions. Confirm bounded provenance and metrics without source leakage, hidden reasoning, provider payloads, Roslyn objects, secrets, or unsafe cross-session deduplication.
10. Compare repeated fixed-task runs against the granular-tool baseline. Confirm equal or better answer correctness with fewer dependent rounds, fewer repeated/contained searches and overlapping reads, lower repeated model-visible source, and no regression in policy, approval, audit, cancellation, semantic confidence, or mutation/build/test authority.

**Verifies:** compiler-aware repository discovery, native tool policy, semantic generation and confidence, context/cache/session governance, source provenance, bounded flow and impact, natural-language structural retrieval, safe model-visible deduplication, associated textual artifacts, interactive/headless parity, and task-sufficient exploration without replacing granular tools or host authority.

---

## Scenario AP - Deployable Prompt Assets and Cached Loading

1. Build and publish Threadsmith for every supported runtime. Confirm each application output, staged payload, archive, and installer contains one flat, case-insensitively collision-free `prompts/` directory whose filenames agree exactly with the code-declared catalog and documented token contracts.
2. Start a deterministic provider capture with the shipped assets. Confirm system/phase/output context, provider instructions, built-in tool descriptions, corrections, skill procedure prompts, parent delegation descriptions/results, delegated-child policy/progress/steering, and `code_explore` guidance preserve their expected roles, order, wording, whitespace, schemas, and host decisions.
3. Stop the process; edit the main system prompt, one ordinary tool description, the `delegate_agents` description, one delegated-child policy or guidance asset, and the native Codex instruction; then restart. Confirm only the corresponding provider-visible content and honest capacity/identity totals change.
4. Edit those files again while Threadsmith remains running. Confirm active and later requests in that process continue using the immutable startup snapshot; restart and confirm the new content is then loaded.
5. Independently remove a required file; introduce invalid UTF-8, NUL, an unresolved/unknown/missing token, a case-only filename collision, an oversized file/catalog, traversal, and a link/reparse target. Confirm startup fails before provider, tool, delegation, skill, or repository activity and ordinary diagnostics expose no body or token value.
6. Expand the Codex instruction near and beyond a selected model's capacity. Confirm it is counted exactly before context admission, reduces lower-priority context while the request can still fit, and fails before network dispatch when fixed content plus tools and output reserve cannot fit.
7. Attempt to use edited assets to advertise or invoke a disabled tool, change a tool or result schema, approve/apply a mutation, widen repository/child trust or paths, let a child delegate, grant process/network/secret authority, change model/role/budgets, alter finding admission, or bypass validation. Confirm every compiled host boundary remains authoritative.
8. Compare ordinary startup/request logs with explicitly enabled raw-model logging. Confirm ordinary logs contain only bounded safe metadata, while the privileged raw log contains the complete provider-visible externalized content but no credentials, authorization headers, or host-only secrets.
9. Upgrade an installation containing local prompt experiments. Confirm the installer replaces the complete shipped defaults without merging local edits, documented backup/restore guidance is accurate, and an incomplete mixed-version catalog fails closed.

**Verifies:** complete cross-platform prompt deployment, exact default compatibility, eager immutable startup loading, deterministic bounded named-token rendering, provider-wire capacity, raw-log boundaries, authority isolation, safe failure, and replace-on-upgrade behavior.

---

## Scenario AQ - External Semantic Freshness and Request Admission

1. Open a disposable trusted multi-project C# repository, select its solution, and wait for compiler-backed semantic loading. With the composer empty, edit one loaded C# document outside Threadsmith and generate duplicate editor save notifications. Without pressing a key, focusing Threadsmith, or otherwise interacting with its console, confirm one settled background cycle prints `External changes detected; updating semantic model...` and one completion. Repeat with an unsent multiline draft; confirm output waits while the draft is nonempty, the draft remains exact, and deleting it back to empty releases the queued output without submission.
2. Query the edited symbol through symbol, reference, implementation, advanced semantic, and `code_explore` operations. Confirm all observe one new published generation and current source identity while unchanged project/document state remains reusable and the ordinary source save does not reopen the complete solution.
3. Hold an external edit in settling and refresh phases, then submit a model request. Confirm submission joins the same single-flight work and no run identity, cancellation state, budget, steering registration, conversation append, model request, or tool call exists before applied version reaches the latest settled dirty version.
4. Change a project/solution, props/targets, package/SDK/reference, analyzer configuration, or document membership input. Confirm one complete reload occurs. Repeat with create/delete/rename ambiguity and watcher error/lost notification; confirm bounded authoritative recovery never claims stale state is current.
5. Deliver another relevant change while refresh preparation is in flight. Confirm the dirty version advances, late/obsolete publication is discarded or followed by one coalesced cycle, and waiters are released only after applied and dirty versions converge.
6. Apply an approved Threadsmith mutation while also producing an unrelated external edit. Confirm exact host-owned watcher echoes reuse the coordinator without an `External changes` message or duplicate refresh, while the overlapping unattributed edit remains external and cannot be hidden by broad suppression.
7. With the workspace clean, run `/semantic_refresh`. Confirm one full refresh is forced and awaited, completion reports duration and resulting confidence, and no model run, conversation message, budget, tool call, or model call is created. Invoke again during an incremental cycle and confirm repeated manual callers share one full follow-up.
8. Inject an infrastructure/currentness failure. Confirm bounded sanitized failure output leaves the workspace dirty and rejects a new model request before run allocation. Repair the condition and use a later change or `/semantic_refresh`; confirm successful recovery releases admission. Repeat with compiler diagnostics and confirm reduced semantic confidence completes rather than becoming infrastructure failure.
9. Rebind repositories and sessions, cancel one waiter, and shut down while work is active. Confirm obsolete monitor/results cannot publish into the new binding, waiter cancellation does not cancel shared work, shutdown remains bounded, and no source text, raw changed paths, secret, watcher/Roslyn/MSBuild object, exception dump, or terminal type enters the public lifecycle.
10. Repeat submission and forced refresh through the headless surface. Confirm it uses the same command/coordinator and freshness invariant and returns the same structured refresh outcome without interactive prompting.

**Verifies:** repository/solution lifecycle and path policy, immutable Roslyn generations and semantic confidence, workspace-scoped monitoring/coalescing/stable identity, incremental versus full refresh classification, dirty/applied convergence, single-flight admission/manual/background coordination, exact host-mutation attribution, serialized draft-safe TUI lifecycle projection, headless parity, bounded cancellation/rebinding/recovery, and pre-`RunId` stale-state exclusion.
