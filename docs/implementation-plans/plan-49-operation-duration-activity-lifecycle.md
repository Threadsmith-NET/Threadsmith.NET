# Plan 49 — Operation Duration Display and Transient Activity Lifecycle

**Milestone:** M18 — Operation Visibility and Codex Provider Support

**Prerequisites:** plans 02–03, 08, 18–19, 21–22, and 24–28

**Depends on by:** future activity-history inspection, performance diagnostics, and richer operation-progress projection

**Status:** Implementation and focused automated coverage complete; maintained real-terminal, real-transport, precedence/restart, and responsiveness closeout remains under MTP-207–209.

## 1 Objective

Display useful elapsed time while an interactive request is processing and authoritative final durations for built-in/extension tool execution and MCP invocation. Duration display defaults to enabled and is controlled by one standard layered Boolean setting, `tui:showOperationDurations`, available at user and repository scope.

Make `THINKING` strictly transient. It appears while the host is awaiting model work, yields to active tool or MCP activity, resumes while the model processes a tool result, and disappears when final visible model output, a terminal error, cancellation, or completion arrives. A completed turn does not retain a `THINKING` transcript marker. `/thinking` and `Ctrl+T` continue to reveal the latest sanitized transient reasoning when available without making hidden reasoning durable.

## 2 Architectural Context

The conversation-first TUI already projects `TaskIntentRecorded`, model reasoning/output, and tool start/completion events through `ConversationalShell` and `ConversationTranscript`. Its live Spectre status owns a transient composer-adjacent row, while completed tool activity becomes a plain-text transcript marker. Plan 24 supplies semantic `ThinkingIndicator`, `ToolSuccess`, and `ToolFailure` roles; Plan 26 requires native-scrollback-safe rendering; Plan 46 separates hidden reasoning from durable history.

Tool completion events currently identify invocation and outcome but carry no authoritative duration. The TUI can infer approximate time between event delivery timestamps, but queueing, batching, persistence, and rendering delay make that unsuitable as operation truth. The tool pipeline already owns the exact execution boundary and telemetry span. MCP imported tools traverse the standard pipeline, but the current public projection does not expose stable host-owned source identity or remote-invocation duration. Plan 49 adds the minimum provider/SDK-neutral metadata needed for correct presentation and reuses existing telemetry clocks rather than creating a second timing authority.

The UI remains a projection. Duration display cannot change model routing, tool/MCP policy, approval, timeout, retry, cancellation, budgets, persistence authority, or execution ordering. Terminal redraw cadence must remain bounded and must not reintroduce the activity/console-gate deadlocks fixed before this milestone.

## 3 Scope

- One compiled-default-on Boolean setting: `tui:showOperationDurations`.
- Standard configuration layering, including user and repository configuration, with repository precedence under the existing configuration contract.
- A transient total-turn elapsed timer in the active `THINKING` status.
- Live elapsed time for an active ordinary tool or MCP invocation.
- Authoritative final duration in completed/failed tool and MCP transcript markers.
- Stable host-owned activity source classification sufficient to distinguish built-in/extension tools from MCP imports without terminal code inspecting implementation types.
- Monotonic, testable timing and one invariant culture-independent duration formatter.
- Correct activity transitions across model → tool/MCP → continuation model → final response.
- Interactive TUI, plain-text fallback, configuration example/catalog, user guide, automated tests, and maintained real-terminal tests.

## 4 Non-Scope

- Changing operation timeouts, budgets, retries, cancellation, approval, trust, or scheduling.
- Routing models or tools based on measured duration.
- Persisting high-frequency timer ticks or creating domain events for each display update.
- Adding duration text to model prompts, conversation archive, governed memory, or hidden reasoning.
- A new slash command or separate settings for request, tool, and MCP timing.
- Treating terminal/event-delivery elapsed time as authoritative tool or MCP execution duration.
- Displaying endpoint URLs, raw argument objects, result bodies, secret references, or provider SDK types. Built-ins may opt into one reviewed, bounded, sanitized activity detail such as a file path or process command; extension and MCP arguments remain hidden by default.
- Adding wall-clock timestamps, performance charts, historical percentile views, or a general profiler.
- Changing stable headless machine-readable output. Existing structured telemetry and diagnostic data remain authoritative outside the interactive display.
- Showing duplicate generic-tool and MCP completion rows for one imported MCP invocation.

## 5 Current State

- `ConversationalShell` starts transient `THINKING`/`TOOLS` status from domain events and serializes status shutdown before transcript writes.
- `ConversationTranscript` retains a collapsed `THINKING` marker on completed reasoning turns and, as superseded by Plan 73, a compact two-line `TOOLS: <name> - completed|failed` block for tool outcomes.
- `IConsoleSurface.ShowStatusUntilAsync` accepts static text and a completion task; it does not own a bounded elapsed-time update contract.
- `ToolInvocationStarted` includes invocation, name, run, and requester. `ToolInvocationCompleted` includes outcome but no duration or source.
- `ToolInvocationPipeline` owns execution, policy, approval, cancellation, event publication, and telemetry and is the correct ordinary-tool timing boundary.
- `McpImportedTool` adapts SDK transport calls to `ITool`, but MCP source and remote duration are not available as a stable TUI projection.
- Existing tool/model/MCP telemetry already measures latency; display must reuse or align with these boundaries.
- Current scalar configuration conventions support compiled defaults plus machine/user/repository/session/CLI/environment layering.

### 5.1 Task 1 inventory and selected contract

The implementation inventory established these constraints and decisions:

- `ToolInvocationPipeline` already accepts `TimeProvider` and records `ToolInvocationResult.Duration`, but it derives elapsed time from `GetUtcNow()` and starts before resolution, policy, hooks, budgets, and approval. It is therefore neither monotonic nor the required execution-only boundary. The pipeline will retain timing ownership but capture a `GetTimestamp()` value immediately before `ITool.ExecuteAsync` and use `GetElapsedTime(...)` for ordinary-tool execution and its existing latency metric.
- `ToolInvocationStarted` and `ToolInvocationCompleted` are the shared durable/TUI projection boundary. They will evolve additively with optional/defaulted host-owned source, outcome, and elapsed-millisecond fields. No second TUI-only completion DTO or event stream will be introduced. Legacy JSON can omit the additions and continue to deserialize to unknown source/no duration under the existing allow-listed serializer and tolerant restorer.
- `ToolRegistry` currently distinguishes constructor-supplied built-ins from dynamic registrations only internally and does not expose origin metadata. Dynamic extension proxies and MCP imports both use `RegisterOrReplace`, so source cannot be inferred safely from registration shape or implementation type. Registry entries will carry a closed host-owned source descriptor supplied explicitly by built-in composition, `CapabilityRegistry`, and `McpAdapter`; resolution will return the tool plus that immutable descriptor to the pipeline.
- `McpImportedTool` owns the actual `IMcpTransport.InvokeAsync` boundary, including transport-managed response handling, and is the narrow SDK-neutral location for remote timing. It will use injected `TimeProvider` monotonic timestamps and return bounded host-owned invocation metadata through `ToolExecutionEnvelope`. The pipeline will project that MCP duration instead of its outer generic execution duration, yielding one MCP event row rather than parallel generic and MCP events.
- Extension tools remain timed by the ordinary centralized pipeline. `CapabilityRegistry` already knows the owning extension identity and will supply extension source metadata when registering its `CapabilityProxy`; extension implementation types remain absent from events and durable state.
- `IConsoleSurface.ShowStatusUntilAsync` owns one static Spectre status while holding the serialized console gate. `ConversationalShell` owns activity replacement and transcript ordering, while `ConversationTranscript` currently materializes completed `THINKING` and generic tool markers. The later rendering tasks will replace the static string with one terminal-neutral dynamic activity model while preserving the existing single gate and stop/observe/write/start order; no timer tick becomes an event or transcript line.
- `ConfigurationBootstrap` already provides the required compiled → machine → user → repository → session → CLI → environment → secrets precedence. The new Boolean will use that root and one immutable shell-session options snapshot; malformed scalar handling must be added locally because direct `GetValue<bool>` binding does not provide the plan-required warning-and-default behavior.
- No ADR is required for this selection: timing remains owned by the established tool and MCP execution boundaries, events remain the common projection stream, and the TUI remains presentation-only.

## 6 Proposed Design

### 6.1 Effective configuration

Add `tui:showOperationDurations` with compiled default `true`. Load it through the standard configuration root; do not add a parallel theme or TUI-only file. User configuration applies across repositories and repository configuration may override it. A missing value resolves to `true`. An invalid scalar produces the existing bounded configuration diagnostic and uses the compiled default rather than terminating the interactive session.

Capture one immutable effective display-options snapshot for a shell session. Runtime file watching and a duration-specific slash command are not required. All duration surfaces consult the same snapshot, so the feature cannot be partially enabled.

When `false`:

- transient activity still shows `THINKING`, `TOOLS`, or `MCP` without elapsed text;
- completed tool/MCP markers retain name/source/outcome but omit duration;
- execution timing, telemetry, timeouts, and persisted authoritative operation events remain unchanged.

### 6.2 Timing ownership

Use monotonic elapsed time through injected `TimeProvider` timestamps or an equivalent host-owned monotonic abstraction. Do not subtract wall-clock `DateTimeOffset` values for authoritative duration.

- **Request display elapsed:** presentation-only total elapsed from accepted `TaskIntentRecorded` for the current turn until first non-whitespace final `ModelOutputObserved`, terminal failure/cancellation, or `RunCompleted`. The clock continues while tool/MCP work occurs so resumed `THINKING` shows total user-perceived turn elapsed rather than restarting at zero.
- **Ordinary tool duration:** measured by `ToolInvocationPipeline` around the authorized execution boundary immediately before `ITool.ExecuteAsync` through completion/failure/cancellation. Policy/approval waiting is not labeled tool execution and remains independently observable.
- **MCP invocation duration:** measured around the actual remote transport invocation inside the host MCP adapter, including configured transport retry/response handling for that logical invocation but excluding pre-invocation tool policy/approval waiting. Return it through bounded host-owned metadata; do not leak SDK response types.

One imported MCP tool renders one MCP-specific activity/completion row using the MCP duration. It does not also render a duplicate generic tool row. If an older/restored event lacks duration or source metadata, render the legacy marker without a fabricated value.

### 6.3 Activity projection and source

Add a closed host-owned source/activity classification, with names finalized during implementation, that can represent at least `BuiltInTool`, `ExtensionTool`, and `Mcp`. Capture it from registry/adapter metadata when the invocation is resolved; the TUI must not use `is McpImportedTool`, assembly names, or string-prefix heuristics.

Extend the minimum host-owned start/completion projection or event schema with:

- invocation identity;
- bounded display name;
- closed source kind;
- optional sanitized MCP profile/server display identity where already approved for status;
- authoritative elapsed duration on completion;
- outcome (`completed`, `failed`, `cancelled`, `timed out`) where the existing contract distinguishes it.

Schema evolution must remain backward/restoration tolerant. Existing rows without new fields deserialize with unknown source/no duration and retain prior behavior. Duration is non-negative, bounded to the operation timeout/backstop, and serialized in one deterministic unit such as integer elapsed ticks or milliseconds; public APIs should prefer `TimeSpan` only when repository serialization conventions support it consistently.

### 6.4 Rendering and formatting

Use one terminal-neutral formatter for transient and completed durations. Formatting is invariant, compact, and deterministic:

- below one second: integer milliseconds, for example `47ms`;
- one second through 59.9 seconds: one decimal second, for example `8.6s`;
- one minute or longer: `m:ss`, extending to `h:mm:ss` when required.

Examples when enabled:

```text
THINKING · 8.5s
• TOOLS: datetime - completed · 4ms
  └ no additional detail
• TOOLS: github/get_issue - completed · 1.2s
  └ mcp GitHub; issue 42
```

Examples when disabled:

```text
THINKING
• TOOLS: datetime - completed
  └ no additional detail
• TOOLS: github/get_issue - completed
  └ mcp GitHub; issue 42
```

Use semantic roles, not literal color. Active request timing uses `ThinkingIndicator`; active/completed tool/MCP output uses existing tool success/failure/status roles unless implementation evidence justifies a new semantic role. Plain-text/`NO_COLOR` output preserves the same words and durations without control sequences.

### 6.5 Bounded live updates

Extend the console-surface activity boundary with a terminal-neutral dynamic status model or elapsed-start input; do not implement an unbounded timer that writes transcript lines. Refresh no faster than four times per second and only when formatted text changes. The refresh loop is cancellation-aware, uses the same serialized console gate, and is always joined/observed on completion.

Starting, replacing, and ending activity must preserve this order:

1. determine the next activity state from the ordered event batch;
2. signal and observe the prior live status;
3. render pending durable transcript output;
4. start the next live status after output releases the console gate.

No timer callback writes directly to the transcript or races selectors, streaming output, resize, paste, cancellation, or shutdown.

### 6.6 Turn lifecycle

For a normal response:

1. accepted turn → `THINKING · <total elapsed>`;
2. first non-whitespace final answer output → stop and clear `THINKING` before rendering answer;
3. completion/error/cancellation with no answer → clear status before rendering host outcome.

For a tool/MCP continuation:

1. accepted turn → `THINKING · <total elapsed>`;
2. invocation starts → replace with `TOOLS ... · <operation elapsed>` or `MCP ... · <remote elapsed>`;
3. invocation completes → clear live activity, append one completed/failed marker with authoritative final duration;
4. continuation model request → resume `THINKING` using the original total-turn start time;
5. first non-whitespace final answer or terminal outcome → remove `THINKING` permanently.

Whitespace-only model chunks, tool-call framing chunks, reasoning chunks, and usage chunks do not create a completed `THINKING` marker and do not terminate the total-turn clock. A completed transcript contains no `THINKING` word unless the model itself emitted that word as visible answer content. `/thinking` remains available for the latest sanitized reasoning.

## 7 Public Contracts

Names may be refined after inspecting local conventions, but implementation must preserve these responsibilities:

```csharp
public sealed record TuiDisplayOptions
{
    public bool ShowOperationDurations { get; init; } = true;
}

public enum ToolActivitySourceKind
{
    Unknown,
    BuiltIn,
    Extension,
    Mcp,
}

public readonly record struct OperationDuration(long ElapsedMilliseconds);
```

The final design may extend `ToolInvocationStarted`/`ToolInvocationCompleted` compatibly or add a projection-owned activity DTO if changing durable event shape would create unnecessary migration risk. In either case:

- tool/MCP timing is produced outside `Threadsmith.Tui`;
- MCP SDK and transport types remain inside `Threadsmith.Mcp`;
- terminal-library types remain inside `Threadsmith.Tui`;
- completed duration/source survives event batching and is available identically to interactive/headless projections, even though only the interactive display setting controls textual timing;
- high-frequency request timer ticks are never public domain events or durable records.

## 8 Project/File Changes

Expected changes, finalized after code inspection:

- `src/Threadsmith.Core/` — compatible host-owned activity source/duration event or projection contracts if the current event schema is the least duplicative boundary.
- `src/Threadsmith.Tools/` — monotonic ordinary-tool execution measurement and source propagation from registry metadata.
- `src/Threadsmith.Mcp/` — remote invocation measurement and bounded host-owned MCP timing/source metadata.
- `src/Threadsmith.Tui/` — display options, formatter, dynamic status rendering, event-to-activity state machine, transient `THINKING`, and duration-bearing tool/MCP markers.
- `src/Threadsmith.App/` — configuration binding/composition and `TimeProvider` wiring where not already available.
- `.threadsmith/config.example` — documented `tui:showOperationDurations` default and repository example.
- `tests/Threadsmith.Milestone1.Tests/` — activity lifecycle, formatting, timer cadence, terminal ordering, and configuration coverage.
- `tests/Threadsmith.Milestone3.Tests/` — tool timing/source/cancellation/failure coverage.
- `tests/Threadsmith.Milestone8.Tests/` and/or `tests/Threadsmith.Milestone9.Tests/` — MCP remote-duration/source/retry/cancellation coverage.
- `tests/Threadsmith.Architecture.Tests/` — boundary checks if new public contracts or references are added.
- `docs/user-guide.md`, relevant operations docs, configuration catalog/example, acceptance/manual tests, milestone status, and DOX.

New project-level files must be included in their owning project with copy-to-output-if-newer when they are runtime content; ordinary source and documentation files do not require output copying.

## 9 Ordered Tasks

1. Inventory current request/tool/MCP clocks, telemetry spans, event schemas, registry source metadata, activity rendering, configuration binding, and restoration compatibility; choose the smallest host-owned contract that avoids duplicate timing authorities.
2. Add deterministic `TimeProvider`-based duration formatting and boundary tests, including zero, subsecond, minute, hour, negative/overflow rejection, and invariant-culture behavior.
3. Add `tui:showOperationDurations` with compiled default `true`, standard user/repository layering, immutable effective options, invalid-value diagnostics, configuration example, and unrelated-setting preservation.
4. Add stable tool source projection from registry metadata and authoritative ordinary-tool execution duration through success, failure, cancellation, timeout, and synchronous exception paths.
5. Add MCP remote invocation duration around the transport boundary, including retries/response handling, cancellation/timeout, sanitized profile/tool display identity, and no SDK leakage.
6. Evolve durable/projection schemas compatibly; restore legacy events without duration/source and prove no fabricated duration is shown.
7. Refactor the console-surface status contract for bounded dynamic elapsed updates using the existing serialized console gate and observable task lifecycle.
8. Implement the total-turn request clock and model/tool/MCP/continuation state machine, including first-visible-answer termination and terminal outcome cleanup.
9. Remove the completed/collapsed `THINKING` transcript marker while retaining transient reasoning reveal through `/thinking` and `Ctrl+T`.
10. Render source-specific active and completed tool/MCP activity with authoritative final duration when enabled and legacy text when disabled or unavailable.
11. Add deterministic fake-time unit/integration tests, event-batch ordering/deadlock tests, TUI/headless/plain-text tests, restoration tests, and real MCP transport timing tests without wall-clock sleeps where avoidable.
12. Run focused suites, architecture gates, the maintained real-terminal matrix, Scenario S, and configuration user/repository precedence/restart tests.
13. Update user/configuration/operations documentation, milestone/index/status, manual tests, relevant ADR only if the activity projection materially changes ownership, and the DOX chain.

## 10 Testing

### Automated

- Compiled default is enabled; user false disables all three displays; repository true/false overrides user; missing values use true; malformed values warn and use the compiled default.
- One options snapshot controls request, ordinary-tool, and MCP duration display together.
- Fake monotonic time verifies every formatting boundary without real sleeps or culture dependence.
- Request timer starts once per accepted turn, continues across tool/MCP activity, resumes at the original elapsed value, and stops before first visible final output.
- Whitespace/reasoning/tool-framing/usage chunks do not stop the request timer or leave a transcript `THINKING` marker.
- Error, cancellation, timeout, malformed output, event-stream completion, and shell disposal clear/observe timer tasks and leave the composer usable.
- Ordinary tool duration excludes policy/approval wait and covers execution success, failure, cancellation, timeout, and immediate exception.
- MCP duration measures the remote logical invocation, preserves retry semantics, distinguishes source without implementation-type inspection, and renders no duplicate generic tool row.
- Legacy/restored events with missing duration/source render safely without `0ms` or false MCP identity.
- Batching, slow status completion, rapid tool transitions, streamed output, selectors, resize, and cancellation cannot deadlock the console gate or reorder transcript markers.
- Disabled timing performs no periodic duration redraw and preserves existing activity words/outcomes.
- Plain-text/redirected/`NO_COLOR` output contains no ANSI/OSC/control sequences and retains meaningful markers.
- Headless structured behavior and telemetry remain stable; duration display text does not enter model prompts, archive, memory, hooks, or diagnostic content fields.

### Maintained manual/real terminal

- Windows Terminal, common Linux terminals, macOS Terminal/iTerm, SSH, and one multiplexer.
- Fast and slow model responses; built-in tool; extension tool; stdio MCP; SSE MCP; streamable HTTP MCP; retry, failure, timeout, and cancellation.
- User-on/repo-off and user-off/repo-on precedence with restart.
- Native selection, `Ctrl+C`, 10 KB/100 KB paste, streaming, selector use, resize, scrollback, and shutdown while timers update.
- Confirm the completed answer has no stale `THINKING`, timer row, duplicate MCP row, or cursor artifact.

## 11 Security and Permissions

- Duration display grants no trust, tool, MCP, extension, network, secret, process, mutation, or approval capability.
- Repository configuration is untrusted data. It may toggle this display Boolean only and cannot inject labels, formatting, control sequences, timer cadence, or arbitrary types.
- MCP display identity uses existing bounded sanitized profile/tool names; never display endpoints, headers, tokens, MCP arguments, results, or secret references. Optional built-in activity detail is tool-authored from validated input, sanitized again by the host, collapsed to one line, and rune-safely bounded before publication. Process-command detail first masks common named CLI credential switches, including whitespace-separated values; unrecognized credential syntax remains a reason not to place secrets directly on command lines.
- Duration values are bounded numeric metadata. Reject negative, overflowed, non-finite, or impossible restored values and omit display rather than fabricating truth.
- Timer text goes through semantic rendering and plain-text sanitization. No ANSI/OSC/cursor text comes from configuration or remote operations.
- High-frequency timer ticks are transient and excluded from persistence, hooks, model context, conversation archive/memory, and diagnostic bundles.
- Cancellation and timeout remain authoritative host controls; display timers cannot keep an invocation alive or delay shutdown.

## 12 Observability

Reuse existing model, tool, and MCP spans/metrics. Do not emit one log/span/event per display tick.

Record bounded diagnostics for invalid timing configuration, missing/invalid restored duration metadata, and activity-renderer failure. Existing operation spans retain source, outcome, duration, cancellation/timeout, and correlation IDs. Verify displayed final ordinary-tool/MCP duration derives from the same measured boundary as the corresponding telemetry attribute, within deterministic serialization precision.

Add no prompt, reasoning, argument, result, endpoint, or secret content to telemetry. UI refresh count/backlog may be measured in aggregate to prove the four-updates-per-second bound, without repository/session-sensitive labels.

## 13 Migration and Compatibility

- Existing configuration has no key and therefore receives durations enabled by default.
- User or repository configuration can opt out with `tui:showOperationDurations: false`; no configuration rewrite is required.
- Legacy durable tool events without source/duration continue to restore and render their prior marker without duration.
- If an event schema changes, add a tolerant optional field/version migration and update the event catalog; do not rewrite historical rows solely to add unknown durations.
- Existing telemetry exporters continue receiving their current spans/metrics. New bounded attributes are additive.
- Themes remain compatible because the feature reuses semantic roles unless implementation establishes a genuine new semantic category.
- Headless machine-readable output remains compatible. Interactive transcript text intentionally changes by removing completed `THINKING` and adding duration suffixes by default.

## 14 Acceptance Criteria

- With no configuration, an in-progress turn shows a bounded, increasing elapsed duration beside `THINKING`.
- A tool invocation replaces `THINKING` with source-appropriate live activity, emits one completion/failure marker with authoritative duration, then resumes `THINKING` at the original total-turn elapsed time while the continuation model request runs.
- An MCP invocation is distinguishable from an ordinary tool, displays its authoritative remote invocation duration, and does not produce a duplicate generic tool row.
- First non-whitespace final model output, error, cancellation, or terminal completion removes live `THINKING` before permanent output; the completed transcript contains no host-generated `THINKING` marker.
- `/thinking` and `Ctrl+T` still reveal the latest sanitized transient reasoning when available, without persisting or replaying hidden reasoning.
- `tui:showOperationDurations` defaults to `true`, uses normal configuration precedence, and one user/repository value enables or disables request, tool, and MCP duration text together.
- Disabling durations preserves activity/outcome words while stopping periodic duration redraw; execution, telemetry, timeouts, and persistence are unchanged.
- Timing uses monotonic injectable clocks, deterministic invariant formatting, bounded refresh at no more than four updates per second, and no per-tick durable events/logs.
- Tool and MCP final durations come from their owning execution/transport boundaries, not TUI event-arrival subtraction; legacy missing data is omitted rather than shown as zero.
- Activity replacement, output, selectors, streaming, resize, cancellation, paste, shutdown, and slow/failing surface tasks remain serialized and deadlock-free.
- No terminal/model/MCP SDK types cross forbidden boundaries, and no secrets or untrusted control sequences enter timing labels, persistence, telemetry, or transcript output.
- Focused automated coverage, architecture gates, Scenario S, maintained real-terminal/MCP checks, docs, configuration example, status, and DOX pass.

## 15 Risks

- **Timer repaint regresses input/selection latency:** cap refresh at four per second, render only changed text, use one serialized status owner, and run real-terminal bulk-paste/selection gates.
- **Activity lifecycle deadlocks recur:** enforce stop → observe → write → start ordering and cover slow/faulting status tasks plus batched event transitions.
- **Displayed duration disagrees with telemetry:** define one owner per boundary and project the measured result; never reconstruct final tool/MCP duration in TUI.
- **MCP appears twice:** source-classify imported tools and render one MCP row for one logical invocation.
- **Wall-clock changes produce negative time:** use monotonic injected time and reject invalid restored values.
- **Transient reasoning becomes durable through timing state:** persist no timer ticks or reasoning content; retain only bounded operation duration where already appropriate.
- **Default-on output surprises scripts:** restrict textual duration changes to interactive output and retain stable headless structured contracts.
- **Configuration becomes fragmented:** expose exactly one Boolean and no separate duration flags or cadence settings.

## 16 Documentation

- Update `docs/user-guide.md` with default-on examples, disabled examples, total-turn semantics, tool/MCP boundary semantics, transient `THINKING`, `/thinking`, and user/repository precedence.
- Add `tui:showOperationDurations` to `.threadsmith/config.example` and the configuration reference, clearly identifying it as display-only.
- Update relevant terminal/operations documentation with formatting, refresh bound, plain-text behavior, and no completed `THINKING` marker.
- Update `docs/architecture/event-catalog.md` if tool activity events gain optional source/duration fields.
- Maintain Scenario S and MTP-207–209 with real-terminal, MCP, precedence, cancellation, and deadlock coverage.
- Add an ADR only if implementation changes activity/timing ownership beyond the contracts already established by Plans 03, 08, 19, and 24–26.

## 17 Open Decisions

None. Milestone 18 fixes these decisions:

- the single key is `tui:showOperationDurations`;
- its compiled default is `true`;
- it controls interactive request, ordinary-tool, and MCP duration text together;
- live request elapsed is total accepted-turn time and resumes without resetting after tools;
- ordinary-tool final duration is owned by the tool execution boundary;
- MCP final duration is owned by the remote transport invocation boundary;
- one imported MCP invocation renders one MCP-specific row, not duplicate MCP/tool rows;
- `THINKING` is transient and absent from completed transcript output;
- refresh is bounded to at most four updates per second;
- high-frequency display ticks are never durable events.
