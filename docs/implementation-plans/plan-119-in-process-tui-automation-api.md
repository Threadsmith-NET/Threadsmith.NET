# Plan 119 — In-process TUI automation API

**Status:** Planned  
**Delivery track:** M32 — In-process TUI automation  
**Prerequisites:** Existing frontend-neutral interaction coordination and sole TUIKit frontend; interactive session lifecycle; governed conversation/tool execution; hierarchical agent cancellation; application composition/disposal; deployed prompt loading. Integrate with the active checkout's semantic readiness contract, including Plan 118, without duplicating semantic startup or requiring unrelated milestone completion.  
**Related contracts:** [planning governance](planning-governance.md), [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [ADR-51](../architecture/adr-51-frontend-neutral-interaction-coordination.md), [ADR-62](../architecture/adr-62-sole-tuikit-interactive-frontend.md), [M32](milestones/milestone-32-in-process-tui-automation.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

## 1. Objective

Allow another .NET application to create, drive, and asynchronously dispose a Threadsmith.NET instance **in its own process**, while launching the ordinary full TUI. The API automates conversation input; it is not a headless API or another frontend.

The caller supplies an absolute solution/project path, composes messages through the same interaction command path as keyboard input, sees those messages in the read-only composer before submission and in the ordinary transcript afterward, and reads one continuous instance stream containing only newly produced main-agent final model answers. Each new answer is delivered once; previous answers and conversation history are never replayed or appended again. The TUI retains its complete ordinary display, including reasoning when enabled, tools, skills, MCP operations, progress, approvals, and child-agent activity. Shutdown has the same lifecycle authority as `/quit`, including active parent and descendant cancellation and owned-resource disposal.

Automation must not change anything Threadsmith adds to model-visible requests. No automation flag, alternative role, origin label, prompt addition, tool, or special instruction reaches the model.

## 2. Architectural Context

The UI remains a projection and command adapter. `Threadsmith.Interaction` owns interactive coordination; `Threadsmith.Tui.TuiKit` owns terminal input and rendering; Execution owns model/tool work and policy. Hosting owns composition and lifetime, never another engine or conversation loop.

A public reusable library boundary is justified by an external application consuming the host without invoking its executable entry point. Introduce `Threadsmith.Hosting` for the public facade and shared composition/lifetime. Extract the existing composition into it; both `Threadsmith.App` and the public facade consume that same implementation. Keep process argument parsing, exit-code translation, and CLI dispatch in App. Hosting may compose TUIKit but exposes no terminal-library types. Interaction and Core must never reference Hosting.

Do not add a separate automated coordinator, model client, tool dispatcher, agent scheduler, persistence store, semantic loader, or renderer. A small input-admission extension to the existing coordinator and surface is the permitted mechanism. Host-local correlation is necessary for delivery but must stay outside governed/model-visible context.

## 3. Scope

- Public asynchronous instance creation with a required absolute existing solution/project file.
- Ordinary TUIKit startup, normal configuration/authentication/policy, and existing startup readiness.
- Optional complete or partial config/providers JSON replacing the corresponding user-level files independently, with the existing configuration hierarchy and validation unchanged.
- A permanently read-only composer in API-created instances, including ordinary, secondary, and steering composer purposes.
- Programmatic composer population and submission through the existing TUI commit and coordinator paths.
- One continuous instance-level final-answer `IAsyncEnumerable`, with message boundaries and separate per-submission completion outcomes.
- Main-agent/run/response correlation and authoritative final-response classification.
- Bounded input/output admission, deterministic concurrency, cancellation, and stream failure semantics.
- Shared `/quit`, API shutdown, terminal interruption, startup-failure cleanup, and async disposal.
- A buildable consuming application sample and transitive runtime-asset validation.

## 4. Non-Scope

- Headless embedding, HTTP service, IPC, subprocess automation, keystroke simulation, alternate TUI, or UI hosting inside a GUI widget.
- Multiple simultaneous full-screen TUIs sharing a process terminal; permit sequential instances after cleanup.
- Arbitrary public access to DI services, dispatchers, raw domain events, provider responses, hidden reasoning, tool results, or child transcripts.
- Automatic approvals or broader trust simply because an API caller supplies input.
- Public stream replay, broadcast/fan-out, persistent response queues, or general observer SDK.
- Changing interactive CLI composer behavior, headless behavior, or deployed prompt wording.
- New public package publication in this planning change. Implementation must make the library consumable by project reference and establish package asset correctness; publishing requires a separate explicit instruction.

## 5. Current State

The following observations are from the active checkout. Re-read affected files before implementation, especially where concurrent work changes startup.

| Owner | Existing behavior and implication |
|---|---|
| `src/Threadsmith.App/Program.cs` | Composes configuration, prompt cache, foundation, model services, MCP, applications, extensions, and shell. Several lifetimes are method-local. Calling `Main` does not return an independently controllable host. |
| `ConfigurationBootstrap.cs` | Resolves repository identity/configuration paths. API startup must use this policy with an explicit target rather than caller working-directory defaults. |
| `ConfigurationBootstrap.Build` / `BuildTrusted`, `ModelComposition.LoadCatalogsAsync`, `ModelProviderConfigurationLoader` | Config currently reads machine/user/repository layers and existing additional overrides; providers use the existing user/repository catalog parsing and stable-ID merge. Replace only the selected user input source, including the trusted view, and reuse these paths. |
| `InteractiveFrontendRunner.cs` | Creates `TuiKitSurface` and `InteractionCoordinator` and runs them together. This is the production launch path to extend. |
| `ShellRunner.cs` | Registers process-global `Console.CancelKeyPress`, selects interactive/headless mode, and translates outcomes into exit codes. Reusable host shutdown cannot exit or indiscriminately alter the caller process. |
| `InteractionCoordinator.cs` | Opens the repository/solution, waits on startup state, reads composer input, interprets commands, obtains URL consent, invokes `InteractionController.SubmitAsync`, handles approvals/steering, drains output, and cancels the active run in `finally`. `/quit` currently breaks the idle input loop. |
| `InteractionController.cs` | Enforces one active run, validation/review boundaries, and routes through `InteractionPresenter` to typed commands. Calling the dispatcher directly would bypass coordinator behavior. |
| `Contracts/InteractionInput.cs` | Defines `ComposerPurpose`, `ComposerRequest`, and `InteractionInput`; input carries an operation cancellation token. This is a relevant existing seam. |
| `TuiKitSurface.cs` | Owns ordinary/secondary/steering composers, UI serialization, input epochs, autocomplete, paste and submit. `CommitComposerInput` echoes the ordinary input, records history, clears the buffer, and advances the epoch. API input must reuse this commit. |
| `SessionApplication.ConversationLoop.cs` | Publishes sanitized `ModelOutputObserved` for text as model chunks arrive. Later processing discovers tools, plans, corrections, steering continuations, and terminal text. Intermediate text therefore cannot automatically be called a final answer. |
| `Core/Events.cs` | `ModelOutputObserved` contains session/time/text only; no run, response, sequence, or final-answer classification. `RunCompleted` is a lifecycle outcome, not the answer payload. |
| `ConversationTranscript.cs`, `Markdown/ModelAnswerCollector.cs` | Project full visible conversation and collect text until presentation boundaries. A Markdown block boundary is not proof that an assistant response is final. |
| `ApplicationComposition.cs`, `HostFoundation.cs` | Already own scheduler, skills, mutation, scratchpad, semantic, persistence, telemetry, and other disposal. `ApplicationServices.DisposeAsync` continues disposal steps and aggregates failures. Reuse that ownership. |

Current source does not establish that every provider identifies final text before emitting it. Runtime behavior and the complete provider matrix must be assessed in Task 1; do not promise immediate final-token streaming on unsupported providers.

## 6. Proposed Design

### 6.1 Shared host and instance lifetime

Extract a shared host creation routine and lifetime owner from App. It returns an internal composed host, not public subsystem services. Preserve service construction, effective configuration, policy, prompt cache, event subscriptions, and reverse disposal ordering. Remove the competing inline ownership from `Program.RunAsync` once App uses the shared owner.

The public facade creates that host in interactive mode, starts the ordinary frontend/coordinator as an owned task, and returns only when a valid session has reached the same input-ready startup boundary as ordinary TUI operation. Creation must remain asynchronous while the TUI event loop runs; no synchronous wait on the caller's synchronization context.

State is `Starting → Ready → Stopping → Stopped`, with `Faulted` recorded on startup/runtime failure. Admission additionally follows the coordinator's actual input purpose and busy/review state. Ready does not mean `FullSemantic`; it means the existing startup/admission contract is satisfied. A missing provider does not become a new startup failure if normal TUI startup supports offline provider recovery.

Reserve one process-local terminal lease atomically before terminal startup, release it on every failure/stop path, and reject a second simultaneous instance with an actionable host exception. Enforce the same terminal ownership across API and CLI launches in the process. Do not change process working directory, call `Environment.Exit`, permanently redirect Console, or overwrite caller-installed global handlers. No console allocation or external terminal launch is implicit: a GUI/service caller must supply a supported attached terminal. Unsupported or redirected terminals fail honestly rather than falling back to headless mode.

### 6.2 Explicit solution/project selection

Creation options require `SolutionOrProjectPath`. Reject null/empty, relative or drive-relative paths, directories, missing files, and target types the existing loader does not support. Use the existing supported-target rules rather than maintaining another extension list. Normalize once using existing path/confinement rules.

Resolve repository root through the existing repository discovery policy from the target's parent. For non-Git inputs, use the existing permitted standalone-project policy; document its exact behavior. Never scan every solution in the caller's directory or silently select a sibling. Feed the exact target into existing repository binding and semantic startup. Existing trust/configuration controls still apply; trust is not inferred from API invocation. Do not expose an unrestricted configuration/service-registration callback in v1.

### 6.2.1 Caller-supplied user configuration

Add two independently optional JSON strings to startup options: `UserConfigurationJson` and `UserProvidersJson`. Each accepts a complete or partial JSON document in exactly the same format as its ordinary user file. This is a simple source substitution at the user layer, not a new override layer or configuration system.

| Startup option | When supplied | When omitted (`null`) |
|---|---|---|
| `UserConfigurationJson` | Use this JSON instead of the running account's user `config.json`; ignore that file entirely. | Use ordinary user `config.json` resolution and first-launch behavior for the account running the calling application. |
| `UserProvidersJson` | Use this JSON instead of the running account's user `providers.json`; ignore that file entirely. | Use ordinary user `providers.json` resolution for the account running the calling application. |

The choices are independent: supplying only config JSON still uses the account's providers file, and supplying only providers JSON still uses its config file. Supplied `{}` means an intentionally empty user layer; it does not mean omission. A partial supplied document never fills missing keys from the ignored user file. Missing values come from the existing defaults/lower layers or later layers under ordinary merge semantics. Empty/whitespace/malformed input is handled by the same JSON validation as an equivalent user file, not silently treated as omitted or retried against disk.

Preserve **machine → selected user source → repository** precedence and all currently supported default/session/environment overrides in their existing positions. For provider catalogs, reuse their actual existing layer support and stable-ID merge; do not invent a machine providers file or merge arrays by index. Apply the selected user source consistently to both effective and trusted configuration/catalog views, including limits and provider discovery. Treat supplied JSON with exactly user-layer authority, neither repository authority nor a new privileged API override. Repository restrictions remain unchanged.

Use the existing parsers, binders, validation, limits, migration, merge, and diagnostics, extending them only to accept in-memory JSON at the same user-layer slot. Retain normal account-based path bases, credential/secret lookup, and unrelated user assets. Do not read, scaffold, migrate, or validate the replaced disk file, materialize the supplied JSON into that file, change process identity, or create temporary profile directories. Any later rebuilding of configuration/catalogs must keep using the selected source for the instance lifetime. Existing explicit user-setting writes do not switch reads back to the ignored file. No general configuration callback, new persistence API, hot-reload feature, or extra configuration framework is required.

### 6.3 Permanently read-only composer

Add immutable host-owned interaction options indicating external composer ownership. The option applies for the complete instance lifetime, across startup, restored/new/cloned sessions, errors, run completion, modals, and steering. It cannot be toggled by a slash command or repository configuration.

Guard every manual composer mutation: characters, Enter/newlines, delete/backspace, cut/paste, delayed clipboard completions, history, autocomplete/palette insertion, drag/drop if supported, and direct composer-widget key handlers. Existing `CanEdit` checks alone must be audited through all call sites. Display selection/copy, transcript navigation, resize, agent panes, cancellation, and non-composer selection dialogs remain usable under the default in §17.

Secondary and steering text prompts stay read-only too. Suppress a manual steer gesture unless its prompt has a programmatic responder. Do not turn a workflow's read-only text prompt into a dead end: the API must be able to answer a pending `ComposerRequest` through the same composer path. Selection-based approvals remain ordinary TUI selections. Disabled manual editing is presentation/input state only and must not alter tool availability or model context.

### 6.4 Message admission through the normal interaction path

Use a small bounded admission mechanism owned by the coordinator and surface; no detached poller or second command loop. On an accepted call:

1. Validate size using the same composer draft limits; retain exact text without shell escaping or added provenance labels.
2. Reserve the applicable pending composer read and a host-local submission identity atomically. Reject concurrent submissions or an inappropriate input purpose instead of hiding an unbounded queue.
3. On the existing TUI/UI queue, populate the active read-only buffer and invalidate rendering. Obtain a render acknowledgment for at least one frame before committing; do not rely on an arbitrary sleep or simulate individual keystrokes.
4. Invoke the common commit routine once to echo/history/clear the draft. Resolve the same semantic input read the coordinator uses for keyboard input, including cancellation propagation.
5. Let the existing coordinator perform command interpretation, URL consent, controller admission, hooks, archive, context, tools, and run waiting. Correlate the resulting run and its outcome to the accepted submission before output can be lost.

Refactor keyboard submit into that same common commit operation. No API bypass directly into `SubmitRequestCommand`, model invocation, or steering services. Preserve typed slash-command semantics; host-only commands produce zero answer text and a completion outcome, never a fabricated model response. `/quit` through composition invokes the shared stop path. A command that changes sessions keeps external composer ownership and target confinement rules.

Provide a separate host-owned `RespondToComposerAsync` operation for an already pending secondary/steering read. It uses a prompt identity and purpose so stale replies cannot answer a later prompt. It does not submit a new conversation run. Expose pending composer identity/purpose via a narrow query, not a raw event stream. If a workflow requires multiple text responses, each response must target the current prompt. A conversation composition call during a run is rejected; active steering requires the existing pause/admission handshake and the specific prompt responder.

Cancellation before admission has no visible/model side effect. Cancellation after an accepted input follows existing operation cancellation and reports an honest terminal outcome. Do not claim that canceling a method retracts an already archived user message.

### 6.5 Authoritative final-answer classification

The echo stream includes only **main-agent model-authored final answer text**, retaining the ordinary visible source text/Markdown and whitespace, subject to the same sanitization as TUI model output. It excludes reasoning, intermediate commentary before tool calls, tools/skills/MCP inputs and results, child output, host progress, plans serialized as structured control output, host-generated errors, and presentation markup/ANSI sequences.

Introduce a narrowly scoped host-owned execution notification with session/run/response identity, sequence, text, and answer boundary information, or extend an existing suitable notification after tracing all producers. Do not reclassify existing `ModelOutputObserved` globally or remove it: the TUI and existing projections must still see their ordinary events. If a durable event changes, update catalog/schema compatibility and restoration tests; prefer an explicitly transient notification when persistence is unnecessary. The execution notification is shared capability plumbing, not API-origin metadata.

Classification occurs where the ordinary engine knows the accepted model round's outcome, including tool requests, corrective retries, plan publication, phase continuations, provider fallback, and objective completion. A no-tool model round is not sufficient if the run continues into execution or correction. Define and test the final-answer boundary for planning-only and approved-plan execution runs. Do not synthesize a summary with another model request. A run with no model-authored terminal answer produces no stream item; its completion is reported separately and the continuous stream remains open.

**Strictness takes precedence over earliest emission.** A provider/engine response may disclose a tool call after text has started. Such text cannot be yielded speculatively because `IAsyncEnumerable` cannot retract it. Reuse existing bounded round text storage to retain uncertain text until the accepted final-answer boundary; then emit ordered bounded chunks. Immediate delta delivery is allowed only when an existing authoritative signal proves that subsequent tool/continuation output cannot invalidate classification. The API documentation must explain delayed emission on those paths. Do not instruct the model to announce finality or change prompts to solve classification.

Reuse text accepted by the ordinary sanitizer/presentation source path; do not independently sanitize or parse rendered terminal text. Trace sanitizer behavior across chunk boundaries and ensure concatenated API text matches the classified ordinary visible source. Discard text from failed/retried attempts and superseded rounds. Subscribe/register correlation before dispatch to handle synchronous/very fast completion. Isolate sessions, runs, responses, and delegated agents; never select “latest output” by timing or session alone.

### 6.6 Stream consumption and completion

The instance exposes one continuous single-consumer `IAsyncEnumerable` spanning all conversation submissions during its lifetime. Register its live-output delivery boundary before the first submission can run. Each accepted submission returns only its identity, cancellation operation, and independent completion task; it does not own a response stream. Admission starts work eagerly and enumeration never submits a message.

Deliver ordered host-owned final-response chunks carrying only model text plus submission/run/response identity, per-response sequence, and an end-of-message flag on the last text chunk. These boundaries let a continuous reader distinguish successive answers without inserting host labels, separators, completion messages, or cumulative snapshots into the text. Every chunk is a new suffix of that response, never the response-so-far or conversation-so-far. For example, turns returning `Alpha` and then `Beta` yield text whose per-message concatenations are `Alpha` and `Beta`; the second turn never yields `AlphaBeta` or a repeated `Alpha`. Empty final answers and host-only commands yield no item.

Use one bounded instance delivery queue in both bytes and item count. It may retain newly generated unread output before the first reader starts; this is live pending delivery, not restored history. Never preload the transcript, query conversation history, or replay consumed responses when a new turn starts. Bound tentative response storage by the existing execution output limit; define a concrete additional delivery ceiling from those limits in Task 1. Do not block the shared event publisher, UI queue, model execution, or cleanup on an API reader. A slow/absent reader that exceeds the delivery ceiling faults the continuous echo stream explicitly with a host-owned overflow exception while ordinary execution and TUI output continue. Never silently drop chunks or report truncated text as success. Release per-submission registry entries after terminal processing; output retention belongs only to the bounded instance queue.

Per-submission completion reports success, failure, cancellation, host-command completion, or host stopping plus safe diagnostics and stable submission/run identities where available. Echo delivery failure is separate from engine outcome. A run may succeed while the continuous echo has failed due to reader overflow. No reasoning/tool payload is included in exceptions or completion diagnostics. An individual run's failure or cancellation does not terminate the instance stream: report it through that submission's completion and continue waiting for later new final answers. Never manufacture an answer from an error or publish a failed attempt's partial text as a final answer.

Permit one enumeration for the instance lifetime; additional or repeated readers fail explicitly and never replay anything. Canceling enumeration or disposing its enumerator permanently detaches that consumer and releases the delivery queue; it does not cancel the agent. Canceling the accepted composition operation follows its run token. Explicit per-submission cancellation uses the existing run cancellation boundary. Normal stop drains accepted queued final output and ends enumeration once, settles all handles, and wakes blocked readers; fatal host failure faults the stream with safe diagnostics. Cleanup must never wait for a reader to drain. Tests must cover each distinct cancellation ownership.

### 6.7 Shared shutdown and disposal

Extract one stop authority used by `/quit`, the public stop method, terminal lifetime cancellation, and `DisposeAsync`. During an active run, API stop must reach this authority immediately rather than enqueue `/quit` behind the run's idle composer read.

Order the work according to existing lifecycle dependencies: stop admission; cancel parent and linked descendants through the existing controller/scheduler; cancel active input/approvals and tracked tool/process operations; join owned run/agent tasks with existing bounded backstops; drain accepted terminal events and final presentation; dispose frontend; then dispose extension/application/MCP/model/foundation services and subscriptions in their required order. Persistence and event infrastructure must survive long enough to record terminal outcomes. Trace actual ownership before selecting exact order; do not add a second scheduler cleanup.

Use one cached cleanup task so concurrent stop/dispose/terminal cancellation joins the same work. Repeated disposal is safe. Caller cancellation of `StopAsync` cancels that caller's wait, not cleanup once started. `DisposeAsync` uses a host cleanup token/backstop, not an already canceled user token. Continue independent cleanup steps after individual failures and report aggregate safe diagnostics; timeouts must not be reported as complete teardown. Non-cooperative work uses the existing abandon-and-discard contract and cannot publish into a disposed/new instance. Verify extension unload behavior and terminal restoration rather than assuming GC performs teardown.

## 7. Public Contracts

Proposed public surface in `Threadsmith.Hosting`; final names may be adjusted together before Task 2, but these semantics are binding:

| Contract | Required behavior |
|---|---|
| `ThreadsmithInstance` | Sealed asynchronous lifetime owner implementing `IAsyncDisposable`; no service provider or subsystem objects exposed. |
| `CreateAsync(options, cancellationToken)` | Returns a ready instance after ordinary TUI startup, or throws a safe startup exception after partial cleanup. |
| `ThreadsmithStartOptions.SolutionOrProjectPath` | Required absolute file path; no implicit current-directory target. Existing normal configuration selects model/provider and policy. |
| `ThreadsmithStartOptions.UserConfigurationJson` | Optional complete/partial user config JSON; when non-null replaces user `config.json` entirely at its existing layer; otherwise use the running account's file. |
| `ThreadsmithStartOptions.UserProvidersJson` | Optional complete/partial user providers JSON; independently replaces user `providers.json` entirely at its existing layer; otherwise use the running account's file. |
| `ComposeMessageAsync(text, cancellationToken)` | Eager, bounded admission through the ordinary conversation composer; returns `ThreadsmithSubmission`. Busy/concurrent/invalid-purpose calls fail explicitly. |
| `ThreadsmithInstance.ReadFinalResponsesAsync(cancellationToken)` | One continuous single-consumer `IAsyncEnumerable<ThreadsmithFinalResponseChunk>` across submissions; only new final answer source chunks, no history/replay; reader cancellation detaches only the reader. |
| `ThreadsmithFinalResponseChunk` | Immutable model text plus submission/run/response identities, per-response sequence, and end-of-message flag; no reasoning, tool, status, or accumulated conversation payload. |
| `ThreadsmithSubmission.Completion` | Task of immutable host-owned completion DTO; accessible without enumerating. |
| `ThreadsmithSubmission.CancelAsync(cancellationToken)` | Cancels that submission's active run through normal authority; terminal handles are idempotent. |
| `GetPendingComposerAsync(cancellationToken)` | Narrow immutable pending-prompt identity/purpose, or none; no raw UI/engine object. |
| `RespondToComposerAsync(promptId, text, cancellationToken)` | Populates and commits the matching secondary/steering composer through ordinary coordination; rejects stale identities. |
| `StopAsync(cancellationToken)` | Initiates shared `/quit`-equivalent shutdown and waits; caller wait cancellation does not undo cleanup. |
| `Completion` on instance | Reports frontend lifetime termination and safe host outcome, allowing callers to detect terminal interruption/runtime failure. |

Use host-owned identities/enums/DTOs only; no provider SDK, Roslyn, extension implementation, Markdig, TUIKit, terminal handles, or live internal cancellation sources in public contracts. Every async method and enumeration accepts/propagates cancellation. Document post-stop and post-disposal behavior, exception categories, and single-consumer rules.

## 8. Project/File Changes

- Add `src/Threadsmith.Hosting/` and its project to `src/Threadsmith.sln`: facade, options/DTOs, submission handles, shared host lifetime/composition, and bounded delivery adapter.
- Move the reusable parts of App's `HostFoundation`, `ApplicationComposition`, `ModelComposition`, `IntegrationComposition`, configuration/path/bootstrap records, and frontend runner to that owner as required by actual dependencies. Retain process/CLI-only responsibilities in App. Preserve namespaces internally where it prevents unnecessary churn; eliminate duplicate implementations.
- Extend shared configuration bootstrap and provider catalog loading to substitute optional in-memory JSON for each user source, including effective/trusted reads and catalog discovery; reuse existing parsing and merging rather than implementing another config pipeline.
- Update App project references and `Program`/`ShellRunner` to use the shared host; keep external command-line behavior compatible.
- Extend Interaction contracts/coordinator/controller at the existing composer-read, admission, completion, and cancellation seams. Extend TUIKit surface/composer input policy and shared commit operation.
- Add final-answer classification/correlation at Execution's ordinary model/run boundary; update Core event contracts only if the chosen event route requires it.
- Extend existing Interaction, TUIKit, Execution, architecture/composition, integration, and end-to-end tests. Add Hosting contract tests in the most appropriate existing test project or a focused new project only when justified.
- Add `samples/Threadsmith.Hosting.Automation/`: ordinary external consumer, explicit target argument, final-response enumeration, prompt-response example, and `await using` lifetime.
- Reuse build asset declarations for deployed prompts, config examples, docs/skills, worker dependencies, native embedding/reranking assets, MSBuild requirements, and packaged tools. Determine the complete closure from existing publish rules; a project reference must not lose assets previously owned solely by App.

These are ownership targets, not permission to relocate unrelated subsystems. Changes to central packages, build/analyzer configuration, or dependencies require concrete necessity.

## 9. Ordered Tasks

1. **Trace and lock contracts.** Re-read AGENTS, guardrails, startup/coordinator/input paths, every model-text producer and run terminal boundary, scheduler cancellation, extension unload, and asset deployment. Record exact final-answer definitions for each existing run/provider path, resource ceilings, terminal assumptions, and §17 decisions. Capture baseline CLI/headless behavior and model request fixtures. Do not begin C# edits before reading guardrails.
2. **Extract shared composition/lifetime.** Add the reusable library and move existing ownership; make App consume it before adding automation. Preserve configuration, prompt loading, runtime assets, failure cleanup, and exit semantics. Add the two optional JSON inputs as independent user-source substitutions through existing effective/trusted configuration and provider loading. Run focused composition/dependency tests.
3. **Establish startup/terminal ownership.** Validate target, reserve lease, start normal frontend asynchronously, add readiness/termination handshakes, and prove canceled/failed startup releases resources and terminal state.
4. **Add immutable composer ownership.** Guard all mutation routes and prompt purposes, preserve read/navigation/selection controls, and keep normal CLI behavior unchanged.
5. **Add shared input admission and commit.** Refactor keyboard/API commits into one operation; implement visible draft frame acknowledgment, exact text submission, host-local correlation, prompt responders, busy/stale rejection, and cancellation races. Exercise URLs, slash commands, session transitions, and run steering through real coordinator entry points.
6. **Add authoritative answer boundaries.** Implement run/response identities and final classification through existing execution. Keep all original display notifications. Verify late tool calls, phase continuation, retries, fallback, child runs, plans, and empty final output before wiring the public stream.
7. **Implement the continuous instance echo and submission handles.** Register before the first dispatch; deliver each new classified response once using delta chunks and explicit message boundaries; define byte/item ceilings, overflow failures, independent completion outcomes, consumer detachment, registry cleanup, and no-reader behavior. Do not add per-submission streams or history replay.
8. **Unify shutdown.** Route all stop triggers through one cached cleanup task; exercise idle/startup/model/tool/approval/child/semantic states and failure aggregation. Verify sequential restart and no late cross-instance publication.
9. **Validate external consumption.** Build/run the sample from a separate consuming output directory and packaged layout. Prove prompts/config/worker/native/tool assets resolve without depending on the Threadsmith source tree or caller working directory.
10. **Adversarial integration review and documentation.** Trace actual entry points beyond the diff, verify reuse and proportional bounded work, update acceptance/manual/user/architecture contracts, run relevant checks, and record remaining platform/provider evidence honestly. Mark this plan implemented only when acceptance criteria pass; milestone status remains owned by `milestones.md`.

## 10. Testing

### 10.1 Deterministic tests

- Absolute/relative/drive-relative/missing/directory/unsupported/Unicode/spaced target paths; exact solution binding and repository confinement; no change to process current directory.
- Config only, providers only, both, and neither supplied: select each user source independently; omitted inputs use the running account's normal paths. Complete/partial supplied JSON and `{}` replace rather than augment the corresponding disk file; ignored keys/providers never reappear. A malformed ignored disk file cannot affect startup, and a missing replaced file is not scaffolded.
- Supplied JSON and an equivalent ordinary user file produce equivalent effective/trusted configuration and provider catalogs, including machine/default inheritance, repository precedence, existing session/environment overrides, stable-ID provider/model merging, validation, limits, discovery, and ordinary malformed/invalid-value errors. A later rebuild retains the selected source; no temporary profile or supplied-JSON disk write is introduced.
- Readiness handshake, no model submission before readiness, supported degraded/offline startup, terminal conflict, unsupported terminal, partial composition failure and cancellation.
- Every composer mutation route for ordinary/secondary/steering reads; delayed paste/autocomplete epoch races; resize/modal/session transitions; normal CLI composer remains editable.
- Programmatic draft is visibly rendered before common commit; exact multi-line text reaches ordinary intake/archive/context once; history and transcript echo occur once; size and concurrency failures are atomic.
- URL consent, hooks, policy, selections, host commands, invalid slash commands, and stale prompt replies use the established coordinator. API stop during an active run never waits for idle input.
- Scripted provider emits reasoning, commentary, tool/skill/MCP calls/results, child text, final Markdown, whitespace, and terminal outcome: API gets only the classified main final source; TUI receives all ordinary items.
- Several consecutive submissions share the same active enumerator: each new answer appears once with correct boundaries and correlation, previous answers are never repeated, and restored/new/cloned session history is never fed into the stream. A failed/canceled/host-command-only turn does not close the stream; a later successful turn still arrives.
- Chunks contain only newly delivered suffixes, not cumulative answer snapshots. Joining chunks per response reproduces that response alone; starting enumeration after new output was generated drains only bounded unread live items, never a transcript replay.
- Late tool call after text, no final text, structured-plan-only output, retries/corrections/fallback, phase continuation, fast completion before submission returns, overlapping child responses, and cross-session output.
- Byte/item bounds with many tiny chunks and large chunks; absent/slow reader cannot delay TUI or execution; overflow is observable; duplicate readers fail; enumeration cancellation does not cancel execution; per-submission cancellation does.
- Individual success, failure, cancellation, and command-only completion settle their submission tasks while preserving the continuous reader; overflow, reader cancellation, fatal host failure, terminal stop, and disposal settle the stream exactly once and wake blocked readers.
- `/quit`, public stop, disposal, and terminal cancellation share teardown; stop-wait cancellation does not abort it; faults in one disposer do not skip remaining owners; bounded non-cooperative work cannot publish afterward.
- Model-facing equivalence: scripted manual and API runs with the same target/config/messages/tool outcomes produce equivalent canonical model requests after normalizing IDs/timestamps only. Inspect roles, prompt assets/tokens, sections, archive/memory provenance, available tools, hooks, child context, and provider envelopes for automation-origin leakage.
- Public API/dependency tests reject SDK/compiler/terminal/extension implementation types and reverse references; consuming build verifies transitive deployment assets.

Tests must exercise the shared coordinator/controller and execution boundaries, not only mock the new facade. Add concurrency barriers/fake clocks where helpful; avoid timing-sensitive sleeps and implementation-mirroring tests.

### 10.2 Real terminal and integration verification

Run the sample in maintained Windows and Unix terminal environments. Confirm the full normal TUI starts, startup binds the supplied path, injected text appears in the composer and transcript, manual edits cannot change it, non-composer controls and approvals work, child activity remains visible, and only final answers reach the caller. Repeat with reasoning enabled and Markdown on/off.

Interrupt during provider streaming, tracked process work, semantic loading/warming, approval wait, and a delegated child. Confirm terminal restoration, descendant completion, durable cancellation, released file/store locks, no active tracked resources, and a subsequent instance in the same process. Validate a real provider that lacks early final-channel signaling and record delayed echo behavior. Keep live credentials out of fixtures/logs.

Run focused subsystem tests and dependency/composition gates, then the existing required build/test/release-asset checks appropriate to the final diff. Record environment limitations instead of interpreting missing terminal/provider evidence as a pass.

## 11. Security/Permissions

API input has exactly ordinary user-input authority. Existing trust, approvals, network consent, hooks, mutation lifecycle, tool/MCP/skill policy, secret resolution, and repository confinement remain authoritative. Read-only composer state is not a security sandbox or broader authorization. The model receives ordinary user roles and existing prompt/context contracts only.

Caller-supplied config/providers JSON has exactly the authority of the replaced user layer and is processed by its ordinary validation and trusted/effective views. It adds no special API priority. Omitted input uses the account under which the calling application executes; no impersonation or separate notion of a Threadsmith user is introduced. Do not log supplied JSON or attach it to model context as API provenance.

Sanitize using established host boundaries; raw provider credentials/reasoning/tool results must not escape through stream failures. Avoid logging composed text, secret paths, or provider envelopes in new telemetry. Prompt files/configuration remain data and are not executed. External-owned cancellation/resources are never disposed by Threadsmith.

## 12. Observability

Retain ordinary events, progress, logs, spans, child-agent visibility, and completion display. Add bounded host-side measurements for startup/readiness, submission admission/commit, response classification/first delivery, queued bytes/items, overflow/detachment, and shutdown duration/outcome. Use host-local correlation without prompt/archive automation annotations.

Distinguish execution success from echo delivery failure and shutdown wait cancellation from cleanup cancellation. Do not emit a special API banner into the conversation or tell the model why the composer is read-only. Debug diagnostics may identify the public host call boundary outside model-visible data; they must not be fed back into context.

## 13. Migration/Compatibility

The CLI and headless surfaces retain their current behavior and shared authority. API-only composer ownership is opt-in through public host creation and immutable thereafter. Existing event consumers remain compatible; any event-schema additions need explicit compatibility tests. No persisted automation flag is necessary.

When JSON options are omitted, API configuration is the existing machine/user/repository hierarchy for the calling process's account. Supplied inputs change only the corresponding user source; full and partial documents have the same semantics as user files. Normal CLI file loading is unchanged.

The consumer targets .NET 10 and uses async creation/disposal. Shared composition extraction must preserve App's deployment and test access; update existing friend-assembly/test boundaries deliberately rather than making internals public. Package/project-reference output must carry the runtime assets and platform requirements previously supplied by the executable layout. Document existing platform/native constraints; do not hide asset failure with model or semantic fallbacks invented for this API.

## 14. Acceptance Criteria

1. A separate .NET application creates Threadsmith in-process with an absolute supported solution/project path and sees the ordinary TUI bind that exact target.
2. API-created instances never accept manual composer edits in any composer purpose or lifecycle state; ordinary CLI instances remain editable.
3. Accepted API text appears in a rendered composer frame and follows the common input commit, echo, archive, policy, and coordinator execution path exactly once.
4. Approval selections and other allowed TUI controls retain normal behavior; secondary/steering text prompts can be answered programmatically without bypassing authority.
5. One continuous instance enumeration returns each newly produced main-model final answer once, with explicit message boundaries, exact source ordering/whitespace, and ordinary sanitization. Chunks never repeat prior text, answers, or conversation history. Intermediate commentary, reasoning, tools/skills/MCP, children, and host status never appear.
6. Unknown finality causes bounded delayed delivery, never speculative leakage. Empty final answers, host-only commands, and failed/canceled turns emit no fabricated text and do not close the continuous stream; their outcomes are reported separately.
7. Slow, canceled, or absent readers cannot stall ordinary TUI/model execution; bounded overflow is explicit. Completion and cancellation ownership are documented and tested.
8. Equivalent manual and API workflows produce equivalent model-visible requests; Threadsmith adds no automation provenance to prompt/context/history/tools.
9. Stop/disposal during any active state cancels descendants and tracked operations, records terminal outcomes, restores the terminal, releases owned resources/lease, and leaves the caller process alive.
10. A consuming output directory works with the required transitive assets; sequential restart succeeds; concurrent terminal ownership fails clearly.
11. Focused integration/dependency tests and required build checks pass, and maintained real-terminal/provider evidence or specific unassessed limitations are recorded.
12. Caller-supplied complete or partial config/providers JSON independently replaces the corresponding user file and is treated exactly as that user layer. Omitted inputs use the calling account's normal files; existing precedence, parsing, provider merge, trusted views, and validation remain the same, with no fallback to ignored disk content.

## 15. Risks

| Risk | Required mitigation |
|---|---|
| Provider text precedes a late tool call | Classify at authoritative engine boundary; buffer uncertain text under existing limits; document latency. |
| Matching text by session leaks children/another response | Correlate submission, run, response and sequence before dispatch; explicit main-agent ownership. |
| Refactor creates another composition/execution path | Make CLI use extracted owners first; remove competing implementation and inspect call sites. |
| Read-only policy misses paste/history/widget input | Trace every mutation route and asynchronous callback, including secondary/steering buffers. |
| Busy/secondary prompts strand automation | Explicit prompt identity/responder and admission-purpose checks through ordinary reads. |
| Slow observer blocks publisher/disposal | Nonblocking bounded delivery; explicit overflow/detachment; separate outcome task. |
| Embedded host loses App-owned assets/global isolation | External-output smoke test, shared asset declarations, process-global audit, terminal lease. |
| Shutdown failure leaves descendants/late publications | Existing hierarchical cancellation, bounded joins, generation fencing, aggregate cleanup failures. |

## 16. Documentation

- Add `docs/operations/in-process-tui-automation.md` with creation, absolute-target rules, terminal requirements, controls, compose/respond/stream examples, delayed finality, limits/overflow, cancellation, errors, and shutdown semantics.
- Document the two optional JSON strings, independent replacement and `{}`/omission semantics, calling-account fallback, and ordinary machine/user/repository precedence with a small complete/partial example. Keep configuration guidance tied to the existing pipeline.
- Link the public capability and sample from the user guide/docs index and contributor setup where relevant.
- Add an architecture decision documenting shared hosting, composer ownership, transient final-answer delivery, and dependency direction; update event catalog only if event contracts change.
- During implementation add a stable product acceptance scenario for API-driven TUI automation and manual cases for terminal/input/stream/shutdown verification; assign unused IDs then and cite them here. Do not add plan attribution or status to those catalogs.
- Preserve deployed prompt text. If any prompt filename/purpose/token contract changes despite this plan's no-prompt-change objective, update both prompt documentation owners in the same change and explain the deviation.
- This planning change registers M32, its dependency edges, and one README navigation row; it does not reopen completed milestone contracts.

## 17. Open Decisions

The stream shape is resolved by the caller's clarification; one other question retains an explicit proposed default:

1. **Resolved stream shape:** one continuous instance stream carrying only each new final model response, delivered once. No per-message streams, entire-conversation snapshots, repeated prior responses, or history replay. §§6.6–7 define boundaries, buffering, and independent submission outcomes.
2. **Other TUI controls:** keep non-composer controls and selection-based approvals interactive. All actual text composers stay read-only; API prompt responders cover secondary and steering text. If the entire TUI should be observation-only, revise approval/selection ownership explicitly through existing decision contracts; do not auto-approve or leave workflows waiting for inaccessible input.

Task 1 must settle the exact accepted-final-answer boundaries of planning/execution/steering continuations, supported target rules, delivery byte/item ceilings, shutdown backstops, and runtime asset root policy based on the current implementation. These are implementation investigations within the requirements, not permission to weaken final-only output or model-facing equivalence.
