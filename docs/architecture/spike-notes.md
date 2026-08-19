# Spike Notes (plan-01 tasks 10–15)

Recorded 2026-07-31 from the throwaway spike projects under `spikes/`. These observations feed ADRs 1–6 and later plans. Each spike ends with an automated `PASS`/`FAIL` assertion (plan-01 §10).

| # | Spike | Project | Result | Key observation |
|---|---|---|---|---|
| 10 | Terminal.Gui v2 instance lifecycle + streaming | `Spike.TerminalGui` | PASS (decision superseded) | The spike proved streaming mechanics but did not test interactive latency. Windows input/redraw was unusably slow; ADR-9 first fell back to v1.19 and ADR-15 later retired Terminal.Gui. |
| 11 | MSBuildWorkspace load + symbol find | `Spike.MsBuildWorkspace` | PASS | Roslyn 5.6.0 + `Microsoft.Build.Locator` 1.11.2 loads `src/Threadsmith.sln` and resolves `Threadsmith.App.Program`. `MSBuildLocator.RegisterDefaults()` must run before creating the workspace. |
| 12 | OpenAI-compatible streaming + cancellation | `Spike.OpenAiStreaming` | PASS | `Microsoft.Extensions.AI` 10.8.3 `IChatClient.GetStreamingResponseAsync` → `IAsyncEnumerable<ChatResponseUpdate>`. Fake provider (no keys/network); cancellation propagates as `OperationCanceledException`. |
| 13 | Collectible ALC load/invoke/unload | `Spike.CollectibleAlc` (+ `.Extension`) | PASS | `AssemblyLoadContext(isCollectible: true)` + `LoadFromStream` + `AssemblyDependencyResolver`. Unloads after 1 GC iteration in **Release**; Debug JIT keeps locals alive longer (run Release for the authoritative check). |
| 14 | SQLite event write/close/reopen/read | `Spike.Sqlite` | PASS | `Microsoft.Data.Sqlite` 10.0.10 async APIs; write → close → reopen → read matches. Transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 has a known CVE (NU1903) — plan-18 must pin a fixed version. |
| 15 | Process-tree cancellation | `Spike.ProcessTree` (+ `.Worker`) | PASS | Windows Job Object (`KILL_ON_JOB_CLOSE`) + explicit grandchild kill; Linux process-group `kill(-pid, SIGKILL)`. Pid exchange via temp pidfiles (not stdout piping, which deadlocks). Both child + grandchild die; no orphans. |

## Versions observed

| Package | Version |
|---|---|
| PrettyPrompt | 6.0.4 (active inline composer) |
| Spectre.Console | 0.57.0 (active bounded output) |
| Terminal.Gui | 1.19.0 and 2.4.17 (superseded evidence) |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.6.0 |
| Microsoft.CodeAnalysis.CSharp | 5.6.0 |
| Microsoft.Build.Locator | 1.11.2 |
| Microsoft.Extensions.AI | 10.8.3 |
| Microsoft.Data.Sqlite | 10.0.10 |
| .NET SDK | 10.0.204 |

## Deviations from assumptions

- **Terminal.Gui TTY requirement:** v2 requires a real TTY to init the console driver. The spike includes a headless fallback so CI (no TTY) can still prove the streaming mechanism. plan-03 must account for this in UI tests.
- **Collectible ALC Debug-vs-Release:** the extension unloads reliably in Release; in Debug the JIT keeps locals alive and the `WeakReference` may not die within the retry window. plan-17's unload-verification fixture should run in Release (or use the isolated-method + `LoadFromStream` pattern this spike established).
- **SQLite CVE:** `Microsoft.Data.Sqlite` 10.0.10 pulls a vulnerable transitive `SQLitePCLRaw.lib.e_sqlite3`. Not a blocker for M0 (spike suppressed NU1903); plan-18 must resolve.
- **Process-tree pid exchange:** redirected stdout deadlocks when the child reads the grandchild's stdout while the parent reads the child's stdout. The spike uses temp pidfiles instead. plan-08's process tools should use the same pattern (or async stream reads).

## Plan 26 footer feasibility inventory

Recorded from the production dependency inventory for PrettyPrompt 6.0.4 and Spectre.Console 0.57.0. PrettyPrompt's public configuration surface owns the prompt and completion pane but provides no fixed footer/editor-status integration. Threadsmith's existing adapter already requires a private completion-pane workaround; Plan 26 does not add another private dependency. A reserved-row implementation would require cursor save/restore and redraw coordination around PrettyPrompt input, which would take ownership of native scrollback and cannot establish selection, paste, streaming, selector, and resize safety through public APIs.

**Decision:** ship the mandatory composer-adjacent status row through the existing serialized `IConsoleSurface` boundary. Do not ship a permanently pinned row. This retains ordinary terminal scrollback, performs no mouse capture or alternate-screen transition, emits no footer in redirected output, and reevaluates terminal width before each composer opens. Real-terminal regression results remain tracked in the maintained manual plan.

## Open items for later plans

- plan-03: real-terminal selection and paste-latency checks remain in the maintained manual plan; CI uses a terminal-neutral surface.
- plan-06: `MSBuildLocator.RegisterDefaults()` ordering; non-cooperative cancellation of Roslyn/MSBuild (gap #7).
- plan-07: real OpenAI-compatible adapter implementing `IChatClient` against an HTTP backend.
- plan-17: Release-mode unload-verification fixture; blocked-unload detection.
- plan-18: pin a `Microsoft.Data.Sqlite`/`SQLitePCLRaw` version without the CVE.
