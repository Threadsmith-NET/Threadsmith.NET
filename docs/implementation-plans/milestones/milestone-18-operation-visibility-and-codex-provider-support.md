## Milestone 18 — Operation Visibility and Codex Provider Support  *(plans 49–50)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make interactive latency visible and add first-class authenticated OpenAI Codex model access without weakening execution ownership, terminal responsiveness, provider isolation, credential safety, or context governance. Plan 49 owns duration/transient activity; Plan 50 owns native Codex Responses/OAuth support and authenticated account model discovery.

**Deliverables:**
- One standard layered `tui:showOperationDurations` Boolean with compiled default `true`, available in user and repository configuration.
- A monotonic total-turn elapsed timer in transient `THINKING`, bounded to at most four visual updates per second.
- Authoritative ordinary-tool duration measured by the centralized tool execution boundary.
- Authoritative MCP duration measured by the remote transport invocation boundary, with stable host-owned source projection and no duplicate generic tool row.
- One invariant compact duration formatter and semantic/plain-text rendering for active and completed operation states.
- A deadlock-safe model → tool/MCP → continuation-model → final-output activity state machine.
- Removal of the completed/collapsed host-generated `THINKING` transcript marker while preserving `/thinking` and `Ctrl+T` access to latest sanitized transient reasoning.
- Backward-tolerant event/projection evolution, configuration/example/user documentation, Scenario S, and maintained real-terminal/MCP verification.
- A separately compiled `Threadsmith.Models.OpenAiCodex` provider using native Codex Responses streaming rather than Chat Completions.
- Independent Threadsmith OAuth login/refresh/logout with PKCE/state/issuer/host validation and owner-only user token storage; Pi credentials are never read or copied.
- Authenticated bounded discovery of every model exposed to the Codex account, with deterministic stable profile IDs, credential-free metadata caching, no hard-coded model list, and no runtime Pi dependency.
- Provider-neutral separation of provider maximum output from the smaller per-request output reserve so context budgeting remains valid when both advertised maxima equal the context window.
- Exact sanitized Codex request/stream/auth fixtures, Scenario T, and maintained live-account/cross-platform verification.

**Exit criteria:**
- With no setting, `THINKING` displays increasing total-turn elapsed time; an active tool/MCP replaces it, and continuation reasoning resumes the original total-turn elapsed clock.
- Ordinary tool and MCP completion/failure markers show durations from their owning execution boundaries, not event-arrival subtraction; one MCP invocation produces one MCP-specific row.
- First non-whitespace final answer, error, cancellation, or terminal completion clears live activity before permanent output, and no host-generated `THINKING` remains in the completed transcript.
- One user/repository `tui:showOperationDurations` value controls request, tool, and MCP timing together; it defaults on, follows standard precedence, and when off retains activity words/outcomes with no periodic timer redraw.
- Timer ticks are bounded, monotonic, injectable in tests, non-durable, absent from prompts/archive/memory/hooks, and cannot race streaming, selectors, resize, paste, cancellation, or shutdown.
- Legacy events without duration/source restore without fabricated timing; headless structured behavior and telemetry remain compatible.
- Focused Core/Tools/MCP/TUI/configuration/restoration tests, architecture gates, Scenario S, maintained real-terminal/MCP checks, docs, status, and DOX pass.
- Every distinct model returned for the authenticated Codex account is projected without a compiled list; after restart `/models` reports reasoning, context, reserve, and availability honestly and dispatches the selected model at the next safe boundary.
- Content, transient reasoning, tools/continuations, usage, retry, cancellation, and failures normalize from exact Codex Responses fixtures without provider wire-type or credential leakage.
- OAuth login/refresh/logout, restart, callback/headless completion, owner-only cache, token redaction, and official-host restrictions pass deterministic and maintained live-account checks.
- A provider maximum equal to the context window remains representable with a smaller request reserve and positive governed input capacity; existing providers retain compatible behavior.
- Focused Models/App/Context/TUI/persistence/architecture coverage, Scenario T, MTP-210–213, provider docs, ADR, status, and DOX pass.

**Prerequisites:** Plan 49 requires plans 02–03, 08, 18–19, 21–22, and 24–28. Plan 50 requires plans 07, 18, 23, 26, 31–32, 35, 46, 48, and 49. Timing ownership/source projection precedes Plan-49 rendering; Plan 50 then reuses the stable provider, OAuth, reasoning, switching, context, and status foundations.

**Scope decisions:**
- The single key is `tui:showOperationDurations`; no per-operation toggles or duration slash command are added.
- Request timing is total accepted-turn elapsed and continues across tool/MCP work; completed tool and MCP timing comes from authoritative host boundaries.
- MCP activity replaces rather than duplicates the generic tool row for an imported MCP invocation.
- Interactive display changes do not alter timeouts, budgets, retries, approvals, routing, scheduling, telemetry authority, or headless machine-readable output.
- `THINKING` is transient; hidden reasoning remains transient-only and revealable through existing controls.
- Codex uses its native Responses/OAuth boundary in a separate compiled provider project; it is not emulated through Chat Completions.
- Threadsmith owns an independent OAuth grant and never reads, copies, or mutates Pi credentials or requires Pi at runtime.
- The protected authenticated Codex `/models` resource, not Pi or a hard-coded snapshot, is authoritative for current account availability.
- Provider maximum output and per-request output reserve are distinct bounded concepts; context assembly reserves the latter.
- Repository configuration cannot change trusted OAuth/resource authorities or credential-bearing headers.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
