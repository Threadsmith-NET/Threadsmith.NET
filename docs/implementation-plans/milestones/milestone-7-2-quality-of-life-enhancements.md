## Milestone 7.2 — Quality of Life Enhancements  *(plans 27, 28, 29, 30)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Add operational convenience features: per-repo tool enable/disable with `/tools` command, new built-in tools (date/time, Roslyn scripting), solution selection memory, repo initialization scaffolding, and a configurable mutation approval policy with `/policy` command.

**Deliverables:**
- Tool enable/disable infrastructure with per-repo persistence (`tools:enabled`, `tools:disabled` in `.threadsmith/config.json`).
- `/tools` TUI command for browsing and toggling tool availability (intrinsic + extension tools).
- Essential-tool protection (core tools cannot be disabled).
- Date/time/timezone built-in tool.
- Roslyn C# scripting tool (disabled by default, tool-level config for timeout, output size, sandboxing).
- Tool-level configuration support in repo config (arbitrary key-value per tool).
- Solution selection memory (persist in `.threadsmith/config.json`, auto-load with notification).
- Repo initialization scaffolding (`.threadsmith/` directory + `config.json` from `config.example`).
- Mutation approval policy enum (`ReviewAll`, `ReviewRisky`, `TrustPlan`, `TrustSession`, `AlwaysTrustRepo`).
- `/policy` TUI command for selecting approval policy.
- Persistent "always trust" per-repo setting with revocation.
- Hard guardrails preserved under all trust levels (no destructive Git, no outside-repo writes, no secrets, path validation, scope expansion detection).
- Comprehensive `docs/user-guide.md` covering implemented setup, workflows, configuration, safety, and troubleshooting, maintained alongside future user-facing changes; root README retained as the high-level quick start and repository build/run entry point.

**Exit criteria:**
- The user can list all available tools via `/tools` and toggle any non-essential tool on/off.
- Tool state persists per-repo in `.threadsmith/config.json` and survives restarts.
- Essential tools (file read, text search, code search, grep, bash) cannot be disabled.
- The date/time tool returns current UTC and local time with timezone.
- The Roslyn scripting tool is disabled by default, configurable via repo config, and respects timeout/output-size limits.
- A previously selected solution auto-loads with a notification; the user can change it via the existing solution selector.
- A new repo without `.threadsmith/` offers initialization scaffolding.
- The user can set the mutation approval policy via `/policy` and it applies to all subsequent mutation sets.
- `TrustPlan` applies all mutations covered by the approved plan without further prompts.
- `ReviewRisky` auto-applies ordinary edits but pauses for deletions, config changes, dependency changes, large diffs, or outside-repo changes.
- `AlwaysTrustRepo` persists in `.threadsmith/config.json` and can be revoked via `/policy`.
- Destructive Git operations, outside-repo writes, and secret exposure remain blocked under all trust levels.
- The user guide describes all implemented Milestone 7.2 behavior without presenting planned features as available, and links focused operations references for deeper detail.

**Prerequisites:** M7 (Extension SDK and Runtime), M7.1 (TUI Visual System). No dependency on M8 or later.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
