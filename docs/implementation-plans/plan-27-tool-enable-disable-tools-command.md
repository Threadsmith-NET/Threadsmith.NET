# Plan 27 — Tool Enable/Disable Infrastructure and `/tools` Command

**Milestone:** 7.2 (Quality of Life Enhancements)
**Prerequisites:** plan-08 (tool runtime), plan-16 (capability registry), plan-17 (capability proxy), plan-25 (TUI theme command pattern)
**Depends on by:** plan-28 (new tools), plan-30 (mutation approval policy)

**Status:** Implementation complete; real-terminal verification remains. Tool metadata, repository-scoped enabled/disabled state with atomic JSON persistence, essential-tool protection, registry advertisement/resolution filtering, composition-root wiring, the `/tools` numbered TUI workflow, dynamic extension-catalog activation/replacement/removal, extension source names, and focused state/TUI/runtime tests are implemented.

## Problem

All registered tools are always available to the model. There is no mechanism to disable tools the user does not want, and no TUI surface to inspect or manage tool availability. Extension tools have no enable/disable control distinct from extension load/unload.

## Approach

Add a `ToolStateManager` that tracks per-tool enabled/disabled state, persisted per-repo in `.threadsmith/config.json` under `tools:enabled` and `tools:disabled` (tool id arrays). The `ToolRegistry` consults the `ToolStateManager` during `GetAvailableTools()` to filter disabled tools. Essential tools (file read, text search, code search, grep, bash) are marked `Essential = true` and cannot be disabled.

The `/tools` TUI command lists all registered tools (including disabled ones) with their enabled state, category, and source (built-in vs extension). The user can toggle any non-essential tool on/off using the existing numbered Up/Down/Enter interaction.

## Contracts

### `ToolState` (Core)

```csharp
/// <summary>Tracks enabled/disabled state for tools, persisted per-repo.</summary>
public interface IToolStateManager
{
    /// <summary>Initializes from repo config. Returns immediately.</summary>
    ValueTask InitializeAsync(string repoPath, IRepoConfig config, CancellationToken ct = default);

    /// <summary>Gets the effective enabled state for a tool. Essential tools always return true.</summary>
    bool IsEnabled(string toolId);

    /// <summary>Enables a tool. No-op for essential tools (already enabled).</summary>
    void Enable(string toolId);

    /// <summary>Disables a tool. No-op for essential tools.</summary>
    void Disable(string toolId);

    /// <summary>Persists current state to repo config. Called after any state change.</summary>
    ValueTask PersistAsync(CancellationToken ct = default);

    /// <summary>Gets all tool states for display.</summary>
    IEnumerable<ToolStateEntry> GetAllStates();
}

/// <summary>A single tool's state for display.</summary>
public record ToolStateEntry(
    string Id,
    string DisplayName,
    string Category,
    string Source, // "Built-in" or extension name
    bool Enabled,
    bool Essential);
```

### `Tool` record update (Core)

Add `Essential` property to the `Tool` record:

```csharp
public record Tool(
    string Id,
    string DisplayName,
    string Description,
    string Category,
    string Schema,
    Func<ToolInvocationContext, CancellationToken, Task<ToolResult>> Handler,
    bool Essential = false);
```

### `ToolRegistry.GetAvailableTools` update (Core)

Filter by `ToolStateManager.IsEnabled(tool.Id)`. Essential tools always pass.

### `ToolRegistry.GetAllTools` (Core, new)

Returns all registered tools regardless of enabled state. Used by `/tools` command.

### Config schema (`.threadsmith/config.json`)

```json
{
  "tools:enabled": ["tool-id-1", "tool-id-2"],
  "tools:disabled": ["tool-id-3"]
}
```

- `tools:enabled`: explicit opt-in list. If present, only listed tools are enabled (in addition to essential tools). If absent, all non-essential tools are enabled by default.
- `tools:disabled`: explicit opt-out list. Overrides `tools:enabled` for listed tools.
- Precedence: `tools:disabled` > `tools:enabled` > default (all enabled).

### `/tools` TUI command

- Lists all tools: id, display name, category, source, enabled/essential status.
- Numbered selection (Up/Down/Enter) to toggle enabled state.
- Essential tools show `(essential)` and cannot be toggled.
- State persists immediately to `.threadsmith/config.json`.
- Extension tools show the extension name as source.

## Tasks

### Task 1 — `Tool` record `Essential` property

**Project:** `Threadsmith.Core`

Add `Essential = false` to the `Tool` record. Update all `new Tool(...)` call sites to pass `Essential` where appropriate.

**Essential tools (initial set):**
- `file_read`
- `text_search`
- `code_search`
- `grep`
- `bash`

### Task 2 — `IToolStateManager` and `ToolStateManager`

**Project:** `Threadsmith.Core`

Implement `IToolStateManager` and `ToolStateManager`. Load from `IRepoConfig` keys `tools:enabled` and `tools:disabled`. Persist back to `IRepoConfig` on state change.

### Task 3 — `ToolRegistry` integration

**Project:** `Threadsmith.Core`

- Accept `IToolStateManager` in constructor.
- `GetAvailableTools()` filters by `IsEnabled()`.
- Add `GetAllTools()` returning all tools regardless of state.
- `GetToolInfo()` returns tool metadata for display.

### Task 4 — `ToolStateManager` initialization in `Program.cs`

**Project:** `Threadsmith.App`

Initialize `ToolStateManager` after `ToolRegistry` is populated. Wire into DI.

### Task 5 — `/tools` TUI command

**Project:** `Threadsmith.Tui`

Add `/tools` handler to `ConversationalShell`. Follow `/theme` pattern:
- Fetch all tools from `ToolRegistry.GetAllTools()`.
- Display with enabled/essential status.
- Numbered selection to toggle.
- Persist via `ToolStateManager`.

### Task 6 — Extension tool integration

**Project:** `Threadsmith.Extensions.Runtime`

Ensure `CapabilityProxy`-generated tools report the extension name as source. The `ToolStateManager` tracks extension tools by their proxy-assigned id.

### Task 7 — Tests

**Project:** `tests/Threadsmith.Core.Tests`

- `ToolStateManager` enables/disables tools correctly.
- Essential tools cannot be disabled.
- `tools:disabled` takes precedence over `tools:enabled`.
- `ToolRegistry.GetAvailableTools()` respects `ToolStateManager`.
- Config persistence round-trips correctly.

**Project:** `tests/Threadsmith.Tui.Tests`

- `/tools` command lists all tools.
- Toggling a tool updates state and persists.
- Essential tools cannot be toggled.

## Risks

- Extension tools registered after `ToolStateManager` initialization need their state initialized. Solution: `ToolStateManager` evaluates state dynamically (check config at query time, not at registration time).
- Tool id stability: extension tools use a deterministic id derived from extension name + capability name.

## Verification

- `dotnet test tests/Threadsmith.Core.Tests --filter "ToolState"` passes.
- `dotnet test tests/Threadsmith.Tui.Tests --filter "tools"` passes.
- `/tools` command lists all tools with correct enabled state.
- Disabling a tool removes it from `GetAvailableTools()`.
- Essential tools remain available regardless of config.
- Config persists and survives restart.
