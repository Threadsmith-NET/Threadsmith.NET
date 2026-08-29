# Threadsmith.NET User Guide

Threadsmith.NET is a terminal-first, .NET-native coding harness. It combines conversational assistance with host-enforced repository boundaries, trust levels, tool policy, review, transactional mutation, and validation.

This guide documents the currently implemented user-facing behavior. Features described as planned in implementation documents are not treated as available here until they ship.

## Contents

1. [Requirements and installation](#requirements-and-installation)
2. [Starting Threadsmith](#starting-threadsmith)
3. [Opening and initializing a repository](#opening-and-initializing-a-repository)
4. [Trust levels](#trust-levels)
5. [Using the interactive terminal](#using-the-interactive-terminal)
   - [Active-turn tool continuation compaction](#active-turn-tool-continuation-compaction)
6. [How repository changes are governed](#how-repository-changes-are-governed)
7. [Tools and tool availability](#tools-and-tool-availability)
8. [Model providers, secrets, and reasoning](#model-providers-secrets-and-reasoning)
9. [Repository configuration](#repository-configuration)
10. [Themes and session status](#themes-and-session-status)
11. [Extensions](#extensions)
12. [Governed skills and reusable workflows](#governed-skills-and-reusable-workflows)
13. [Headless and automated use](#headless-and-automated-use)
14. [Persistence, retention, and diagnostics](#persistence-retention-and-diagnostics)
15. [MCP connection profiles](#mcp-connection-profiles)
16. [Safety model](#safety-model)
17. [Troubleshooting](#troubleshooting)
18. [Further reference](#further-reference)

## Requirements and installation

Threadsmith can run from source or from a self-contained release artifact. Source builds require:

- .NET 10 SDK;
- PowerShell for the commands shown in this guide;
- a terminal with ordinary native scrollback and clipboard support for the best interactive experience;
- a model endpoint only when using a configured non-fake model profile.

Clone the repository, then restore and build it:

```powershell
git clone <repository-url> Threadsmith
Set-Location Threadsmith
dotnet restore src\Threadsmith.sln
dotnet build src\Threadsmith.sln
```

To validate the complete repository:

```powershell
dotnet test --solution src\Threadsmith.sln
```

Threadsmith uses centrally pinned dependencies and targets .NET 10. If the SDK cannot select the target framework, confirm `dotnet --info` reports an installed .NET 10 SDK.

Self-contained Windows x64/ARM64 setup executables, Linux x64/ARM64 archives, and macOS Intel/Apple-silicon packages include the matching .NET runtime. Verify the release checksum and select the artifact matching the operating system and architecture. Linux archive installation refuses symbolic-link prefixes, unrelated existing launchers, and non-empty directories that lack an exact matching `.threadsmith-install-root` ownership marker; a first install may target only an absent or empty directory. See [release packaging and publication](operations/release-packaging.md) for platform commands and verification details.

## Starting Threadsmith

### Interactive terminal

From the repository you want Threadsmith to open:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --tui
```

When running from the Threadsmith source tree itself, the shorter form is:

```powershell
dotnet run --project src\Threadsmith.App -- --tui
```

The first `--` ends `dotnet run` options. Arguments after it are passed to Threadsmith.

For a lower-overhead interactive launch:

```powershell
dotnet run --configuration Release --project src\Threadsmith.App -- --tui
```

### Headless request

Headless mode is the default:

```powershell
dotnet run --project src\Threadsmith.App -- "inspect this repository"
```

A headless request can also open a specific repository and solution before submitting the model turn:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo --trust TrustedBuild --solution src\MyRepo.sln "explain the request pipeline"
```

When repository options and a request are supplied together, Threadsmith opens the repository, selects the solution, records the baseline, waits briefly for `PartialCompilation` semantic readiness, and fails closed without submitting the request if semantic tools would be unusable. With no request, Threadsmith performs repository discovery without granting file-read trust:

```powershell
dotnet run --project src\Threadsmith.App
```

### Compiled application

Build once and run the application DLL directly:

```powershell
dotnet build src\Threadsmith.sln
dotnet src\Threadsmith.App\bin\Debug\net10.0\Threadsmith.App.dll --tui
dotnet src\Threadsmith.App\bin\Debug\net10.0\Threadsmith.App.dll "inspect this repository"
```

### Startup arguments

Common arguments include:

| Argument | Purpose |
|---|---|
| `--tui` | Start the interactive terminal. |
| `--repository <path>` | Open a repository other than the current directory. |
| `--solution <path>` | Select a solution or supported project explicitly. |
| `--trust <level>` | Request a trust level without an interactive selector. |
| `--set:<key>=<value>` | Apply a one-process configuration override. Do not use this for real secrets. |

Example:

```powershell
dotnet run --project src\Threadsmith.App -- --tui --repository C:\source\my-repo --trust TrustedRead --solution src\MyRepo.sln
```

## Opening and initializing a repository

Threadsmith uses the current directory as the repository unless `--repository` is supplied. In the interactive terminal, `/open [path]` switches repositories.

Repository discovery:

- recursively detects supported solution and project candidates;
- skips `.git`, `bin`, `obj`, inaccessible entries, and reparse-point subtrees;
- automatically selects a single candidate;
- presents an interactive numbered selector when multiple candidates exist;
- returns an ambiguity result in headless mode rather than choosing arbitrarily.

### Remembered solution

After a successful selection, Threadsmith stores a slash-normalized repository-relative path at `solution:path` in `.threadsmith/config.json`. On a later launch:

1. an explicit `--solution` value wins and refreshes the preference;
2. a valid remembered solution loads automatically with a notification;
3. otherwise normal discovery and selection run.

If the remembered file no longer exists, Threadsmith clears the stale preference on a best-effort basis and resumes discovery. Invalid paths that escape the repository or traverse prohibited/reparse locations fail closed instead of being treated as merely stale.

The JSON representation is nested:

```json
{
  "solution": {
    "path": "src/MyRepo.sln"
  }
}
```

### Initializing an empty repository

When interactive startup finds neither `.threadsmith/` nor a supported solution/project candidate, it offers **Initialize** or **Continue without initialization**.

Initialization creates a minimal UTF-8 `.threadsmith/config.json` containing neutral defaults for solution memory and tool configuration. It:

- never overwrites existing configuration;
- uses atomic same-directory publication;
- rejects linked repository roots or configuration directories;
- does not grant trust or enable privileged tools;
- makes no source-code changes.

Declining leaves the repository unchanged.

## Trust levels

Trust controls what the host may inspect or execute. It is separate from model capability, tool availability, and per-invocation approval.

| Level | User-facing effect |
|---|---|
| `UntrustedInspection` | Loads safe repository configuration and lists candidates without reading repository file content. |
| `TrustedRead` | Reads files, selects solutions/projects, inventories target frameworks, and creates text baselines. It does not execute repository build logic. |
| `TrustedBuild` | Permits restore, MSBuild evaluation, analyzers, source generators, compilation, tests, and approved build/process tools. Repository-controlled code may execute. |
| `TrustedMutation` | Adds explicitly approved repository file mutations under configured roots. |
| `FullyTrustedAutomation` | Enables the highest-trust automation capabilities, including explicitly enabled C# scripting, while hard host guardrails remain active. |

Use `/trust` for the selector or `/trust inspect`, `/trust read`, `/trust build`, or `/trust mutation` in the interactive terminal.

Persisted higher trust can be reused from the per-user repository-facts database. Trust is monotonic during repository use: requesting a lower level does not silently erase a persisted higher grant.

Grant `TrustedBuild` or above only to repositories whose build scripts, analyzers, generators, and tests you trust.

## Using the interactive terminal

Startup displays the Threadsmith identity, repository and solution state, effective model, trust, target frameworks, semantic confidence, and terminal mode. The composer is labeled with the current repository directory name.

Ordinary prompts are conversational. A greeting or question can complete as a normal assistant response. A repository-change request remains in the same model turn, but the model must call the host-owned `propose_plan` tool before governed planning begins.

### Commands

| Command | Purpose |
|---|---|
| `/open [path]` | Open or switch repositories. |
| `/trust [inspect|read|build|mutation]` | Show or change repository trust. |
| `/tools` | Browse and toggle non-essential repository tools. |
| `/plan-policy [name|current|reset]` | Select, report, or revoke the plan approval policy. |
| `/policy [name|current]` | Select or report the mutation approval policy for exact staged diffs. |
| `/extensions` | Browse, load, and unload discovered extensions. |
| `/new` | Checkpoint the current session and activate a fresh empty session. |
| `/resume [session-id]` | Resume an exact durable session or use the repository selector. |
| `/clone` | Create and activate an independent governed copy of the current session. |
| `/models` | Select and persist the active repository provider/model. |
| `/reasoning [level]` | Show or set the reasoning level supported by the active model. |
| `/thinking [on|off]` | Stream future sanitized reasoning, or toggle when no argument is supplied. |
| `/theme` | Select a theme. |
| `/theme <id>` | Apply a theme and save it as the user-level default. |
| `/theme current` | Report the active theme. |
| `/context mode` | Report the effective cross-turn conversation mode. |
| `/context mode <conversation-aware\|governed-memory\|stateless>` | Change mode for the next request. |
| `/context inspect` | Inspect the latest run's included, omitted, retrieved, stale, and reduced context. |
| `/context compact` | Request bounded compaction at a safe turn boundary. |
| `/memory remember repo <text>` | Store an explicit local repository-scoped memory fact. |
| `/memory list repo [active\|stale\|superseded\|forgotten\|rejected\|all]` | List local repository memory and audit rows. |
| `/memory inspect <memory-id>` | Inspect one repository-memory item and its provenance. |
| `/memory supersede <memory-id> <replacement-text>` | Correct an item while preserving audit history. |
| `/memory forget <memory-id>` | Mark an item forgotten without deleting audit metadata. |
| `/memory validate repo` | Recheck stale repository memory that can be validated locally. |
| `/help` | Display available commands. |
| `/quit` | Exit cleanly. |


### Durable session lifecycle

`/new`, `/resume`, and `/clone` use one serialized host-owned transition boundary and require active model, tool, mutation, validation, hook, skill, and delegated work to finish or be cancelled. `/resume` lists only sessions for the currently open repository, newest first; an exact ID from another repository reports a mismatch without changing repository, trust, solution, or working directory.

A resumed session reconstructs tolerant event projections and the sanitized conversation archive, governed memory, mode, persisted usage, and compatible model/reasoning selection. Stale context inspections and provider continuation/cache handles are invalidated. A clone receives new session-local identities and independent future history; it does not duplicate live execution authority, approvals, transactions, leases, credentials, hidden reasoning, or provider transcripts. Clone output includes a copyable `/resume <source-session-id>` return command. See [Session lifecycle operations](operations/session-lifecycle.md).

### Keyboard and clipboard

| Input | Action |
|---|---|
| `Enter` | Submit the composer. |
| `Shift+Enter` | Insert a soft newline. |
| `Ctrl+V` or `Shift+Insert` | Paste clipboard content as one operation. |
| `Ctrl+C` | Cancel current input or an active run; with a native terminal selection, copy that selection. |
| `Ctrl+Shift+C` | Copy a PrettyPrompt editor selection where supported. |
| `Ctrl+T` | On an empty composer, toggle future reasoning streaming. |
| Mouse drag / terminal mark mode | Select across the native transcript and composer output. |

Threadsmith deliberately retains native terminal scrollback and does not enable mouse capture. Terminal-specific selection shortcuts remain controlled by the terminal emulator.

### Reasoning display

Reasoning is hidden by default. While a turn is active, Threadsmith shows transient `THINKING` activity and removes it before the first visible answer or terminal outcome; completed transcripts contain no host-generated `THINKING` marker. During active-turn candidate work, `COMPACTING CONTEXT` temporarily replaces `THINKING`, shows the current before/target token counts, candidate profile, and elapsed time, then emits one bounded completion line with actual before/after/savings/status/profile/duration before `THINKING` resumes. No summary, prompt, source, or tool-result content is displayed by default. `/thinking on` enables live streaming of future sanitized reasoning chunks using the `Reasoning` semantic style, `/thinking off` disables future streaming, and `/thinking` or `Ctrl+T` toggles the same in-session setting. Turning streaming off cannot remove reasoning already written to native scrollback. This does not expose credentials or raw unsanitized provider content.

### Cross-turn conversation context

Conversation continuity is bounded and host-owned. Threadsmith archives only sanitized accepted user requests and final visible assistant responses. Hidden reasoning, provider wire payloads, and raw tool output never enter the conversation archive. Large bodies use the content-addressed artifact store while metadata, hashes, ordering, and provenance remain durable.

- **Conversation-aware** (default): current input, bounded recent complete turns, active structured memory, and deterministically retrieved older memory.
- **Governed-memory-only**: current input plus validated structured memory; no raw prior messages.
- **Stateless**: current input and current-run governed state only. Mode changes preserve the archive for later use.

Structured memory distinguishes user requirements, decisions, constraints, unresolved questions, repository findings, completed work, and rejected/superseded information. Explicit corrections supersede rather than erase prior items. Repository findings require current governed evidence and revision provenance; repository changes make dependent items stale before later retrieval.

### Repository-scoped memory

Repository-scoped memory is separate from session conversation memory. It is local to the current repository identity, stored in the ignored `.threadsmith/threadsmith.db` database, and is not shared or tracked by Git. A repository file, prompt append, skill, hook, or ordinary configuration cannot silently create, authorize, or elevate memory.

Use `/memory remember repo <text>` only for facts you explicitly want retained for future sessions in this repository. Threadsmith sanitizes and bounds the text, records user-command provenance, and stores it as untrusted prompt data. `/memory list repo` includes active and inactive audit rows; `/memory inspect <memory-id>` shows the bounded content, validity, authority, hash, and provenance. Corrections use `/memory supersede <memory-id> <replacement-text>` so the old item becomes superseded while the replacement is preferred. `/memory forget <memory-id>` marks an item forgotten without deleting audit metadata.

Repository-dependent memory with path, symbol, project, or revision support is conservatively marked stale when host-observed repository mutations affect that support. Stale, superseded, forgotten, and rejected memory is omitted from model context until explicit validation or correction changes the state. `/memory validate repo` can reactivate explicit user-authored memory that has no repository-dependent support; unverifiable path/symbol/project/revision-scoped items remain stale. `/context inspect` reports repository-memory inclusion, omission, staleness, and budget rationale alongside ordinary conversation context.

Headless callers use the same host-owned repository-memory commands and JSON DTOs as the TUI. Model-proposed repository-memory candidates remain disabled in this implementation; assistant prose alone is never enough to create durable repository memory.

#### How context optimization works

Threadsmith arranges every model request so content that changes least appears first and request-local content appears last:

1. stable host policy;
2. the applicable repository instruction bundle;
3. instructions for the current execution phase;
4. bounded structured memory and complete recent user/assistant turns in chronological order;
5. current governed state and attributable evidence;
6. the current user input;
7. correlated tool calls and results appended during an otherwise unchanged multi-round request.

This layout gives providers that support exact-prefix caching the best opportunity to reuse an unchanged prefix. It does **not** weaken host behavior: tool eligibility, trust, approval, evidence, model capacity, validation, and recovery are evaluated normally whether a remote cache is used or not. Hidden reasoning is never added to later requests.

Eligible tool definitions are canonicalized into one deterministic inventory. A provider with native tool support receives that inventory through its native protocol rather than receiving a second copy in prompt text. Legacy adapters may receive one deterministic textual inventory. This avoids paying for duplicate schemas and prevents unrelated tool ordering from changing unpredictably.

During an unchanged tool round, Threadsmith freezes the assembled prefix and appends the assistant tool call plus its correlated result. It deliberately rebuilds the request when the execution phase, repository trust or policy, eligible tools, applicable instructions, conversation compaction generation, selected model, or request layout changes. Rebuilding is a correctness boundary, not a cache failure.

Capacity checks use the estimated provider-wire request rather than only visible prompt text. The estimate includes structured content, native or textual tool schemas, provider framing, and the selected model's output reserve. Context reduction occurs before dispatch; a request that still cannot fit fails before contacting the provider.

##### Active-turn tool continuation compaction

Active-turn compaction keeps one long user request from repeatedly sending every earlier tool result until the active main model's context window is exhausted. It applies automatically to ordinary multi-round evidence collection and planning. It does not end the turn, ask the user to resubmit the request, change the active main model, or expose a compaction tool to the model. Candidate generation can optionally use a separately configured auxiliary model profile.

This is different from `/context compact`. The command compacts completed cross-turn conversation history at a safe turn boundary. Active-turn compaction is an automatic pre-sampling operation over tool continuation generated **inside the request currently running**. There is currently no user or repository setting that disables, postpones, or manually triggers the active-turn reliability boundary.

Threadsmith uses these terms:

| Term | Meaning |
|---|---|
| **Frozen context** | The initially assembled host policy, repository instructions, current user input, output contract, canonical tool inventory, and other authority-bearing context. Active-turn compaction never rewrites it. |
| **Complete group** | All sibling assistant tool-call messages produced by one model round followed by exactly one matching result for each call, in call order. An incomplete, mismatched, duplicate, or orphaned call/result set is not compactable. |
| **Delivered group** | A complete group that has already been sent verbatim to the model in a later completed request. |
| **Eligible prefix** | The oldest contiguous delivered groups that can be considered for replacement while newer groups remain exact. |
| **Cumulative summary** | One bounded, explicitly untrusted historical assistant message that replaces an eligible prefix. A later compaction receives the complete previous summary plus newly old raw activity and returns one updated checkpoint. |

The exact sequence is:

1. **Form and deliver a complete group.** Threadsmith buffers sibling calls and results independently, then emits every call before the matching call-ordered results. The next model request receives that group exactly. A newly completed group cannot be summarized on this first delivery.
2. **Estimate before every later model request.** The host estimates the complete provider-wire request, including frozen messages, any active summary, raw continuation groups, native or textual tool schemas, provider framing, and the selected profile's effective request output reserve.
3. **Compare with the pressure target.** The operational target is 75% of the usable selected-model input budget. The usable budget is bounded by both the host request budget and the model context window after its effective output reserve. Below that target, Threadsmith sends the unchanged request.
4. **Select only an old delivered prefix.** At pressure, Threadsmith keeps at least the newest complete group exact and aims to retain the newest 12,000 raw continuation tokens. That target scales downward when the selected profile, frozen request, and 16,384-token summary allowance leave less room. It never splits a sibling group or selects a group that has not already been delivered verbatim.
5. **Prepare a bounded candidate request.** Preflight validates aggregate group, message, source, and file-list bounds and projects all candidate input from one candidate-profile-derived capacity budget. The candidate receives the required task objective and required-first acceptance intent, the complete previous active-turn summary when present, and bounded projections of the selected raw tool activity. Preflight performs no provider I/O and creates no hook, usage, call, or cost record. If the configured candidate profile prohibits sensitive input, preflight rejects it before provider I/O.
6. **Generate a Markdown checkpoint.** An actual candidate attempt uses the separately configured compaction profile when present, or the active main profile as a backward-compatible fallback. The candidate profile may identify a different model or provider and owns candidate context/reserve, maximum output, reasoning, temperature, timeout, retry, sensitivity, and pricing. Candidate requests advertise no tools, use the `Summary` workload when independently configured, set a request-specific model-output ceiling equal to 80% of the summary budget by default, and cross the normal managed before/after model-request hook boundary. They are real model requests: profile identity, reported usage, missing usage, call count, duration, and any partial usage emitted before a later failure are accounted under each attempt's unique identity. One transient retry is allowed, for at most two candidate calls.
7. **Validate the replacement.** The candidate returns one bounded Markdown summary. The host validates schema, the exact cumulative source-group range, host-observed file lists, sanitization, authority markers, and the total rendered summary budget. The host strips any model-emitted `Files read` or `Files changed` sections and appends its own cumulative file lists.
8. **Build and re-estimate the replacement.** The model receives the complete prior summary on update attempts and returns one replacement checkpoint rather than an append-only delta. The candidate activates when the rebuilt complete request is smaller than before. It does not have to get below the pressure target in a single operation.
9. **Replace atomically and continue.** On success, one assistant message labeled as untrusted earlier active-turn history replaces only the selected old prefix. Newer groups and frozen context remain exact, the history rewrite generation increments, and the same user turn continues with the rebuilt provider-neutral request.

For example, suppose one request produces eight complete tool groups. Group 1 becomes eligible only after a later completed request has received it exactly; the same rule independently applies to every later group. If the full request then reaches pressure, Threadsmith might replace delivered groups 1–5 with summary version 1 while retaining groups 6–8 verbatim. If the turn continues and later reaches pressure again, the candidate receives the complete version-1 summary plus the next eligible raw prefix and returns one version-2 checkpoint. The original group results are not deleted from the evidence/audit record.

Current host-owned defaults are:

| Boundary | Default behavior |
|---|---|
| Pressure trigger | 75% of the effective active-main-model input budget |
| Main output reserve | Active main profile's effective request reserve; 8,192 tokens only if no profile reserve is available |
| Newest raw retention target | 12,000 tokens, scaled to current activation capacity; at least the newest complete group remains exact |
| Activated summary budget | 16,384 estimated tokens total |
| Model-written summary output | 80% of the summary budget by default; 13,107 tokens with the default 16,384-token budget |
| Minimum required savings | Any positive rebuilt-request reduction |
| Candidate profile | Trusted explicit profile when configured; otherwise the active main profile |
| Candidate input | Lesser of 65,536 estimated tokens and the candidate profile's available input capacity |
| Candidate source shape | At most 48 groups and 512 aggregate call/result messages |
| Candidate summary shape | One bounded Markdown checkpoint; host appends file lists |
| Task projection | Up to 4,000 objective characters and 32 required-first acceptance items, bounded to 1,000 characters each and 4,000 total characters |
| Candidate attempts | One initial call plus at most one transient retry; two calls total |
| Failure backoff | Skip candidate generation for the next two failed pressure assessments |

To select an independent candidate model or tune trusted summary budgets, put settings in repository-excluding machine or user configuration—not repository configuration:

```json
{
  "context": {
    "activeTurnCompaction": {
      "profileId": "00000000-0000-0000-0000-000000000000",
      "summaryBudgetTokens": 16384,
      "modelOutputBudgetPercent": 80
    }
  }
}
```

The profile must come from the repository-excluding user/machine/host-owned catalog, be enabled, support streaming, and be intended for the `summary` workload or unrestricted by workload. It can reference another provider. Repository-only profiles and repository overrides of a user profile are not visible to this auxiliary dispatcher. Candidate credentials must come from user-owned-or-higher secret providers; repository secret stores cannot supply or replace them. The request-specific provider generation limit is the lower of the profile's effective output reserve and the configured model-output percentage of the summary budget. With defaults, a 16,384-token summary reserve sends `13,107` as the model-output ceiling. Removing the trusted profile setting or setting it to `null` restores the active-main-profile fallback. These settings are resolved at startup; restart Threadsmith after changing them. Repository configuration at this path is ignored, and candidate dispatch uses a separate repository-excluding catalog/provider snapshot, so repository content cannot add, rewrite, or reroute the model that receives candidate evidence. The main profile still owns the 75% trigger, emergency capacity, and rebuilt ordinary request—a smaller candidate profile does not make a large-context main model compact earlier.

Compaction is deliberately lossy only in the model-visible working set. It does not change current user intent, host or repository instructions, trust, tool eligibility, approvals, mutation authority, output requirements, active main model, or sensitivity policy. Tool-result groups are conservatively classified as repository-sensitive for auxiliary routing; a configured candidate profile that prohibits sensitive input fails preflight with no hook or provider call. An active-turn summary never becomes system/developer/current-user content, durable conversation memory, or repository memory. The original sanitized tool events and evidence remain under the existing audit, artifact, retention, and redaction rules. Ordinary active-turn summary checkpoints are kept in memory for the running turn.

Cancellation, hook denial, provider failure, invalid output, validation rejection, and zero-or-negative savings leave the original continuation active and start bounded backoff. If that unchanged request still fits, the turn continues with exact raw groups. If it reaches the emergency boundary, the deterministic compatibility reducer may shorten only older results that were already delivered verbatim. It never shortens a never-delivered group. If the request cannot fit without doing so, Threadsmith fails with a controlled message of the form `Tool continuation requires <tokens> input tokens but the selected model budget is <budget>.`

`/context inspect` shows the latest assessment without exposing summary or tool-result content. Its `active-turn` line reports the status; before/after input estimate; pressure target and maximum; main output reserve; effective/configured retention; candidate profile ID; eligible, compacted, and retained group counts; retained tokens; summary version; cumulative pruned-item count; history generation; remaining backoff; and host rationale. Status values mean:

| Status | Meaning |
|---|---|
| `Disabled` | Host composition did not provide active-turn compaction. |
| `BelowPressure` | The canonical complete request is below the operational target. |
| `NoEligiblePrefix` | Pressure was reached, but no complete previously delivered prefix can be cut. |
| `Backoff` | A prior failure temporarily suppressed another candidate attempt. |
| `Completed` | A validated cumulative summary atomically replaced the reported prefix. |
| `ValidationRejected` | Candidate schema, fact, source, authority, sensitivity, range, or bound validation failed. |
| `ProviderFailure` | Candidate generation exhausted its bounded call budget or was denied at the managed provider boundary. |
| `Cancelled` | Cancellation retained the original continuation. |
| `InsufficientSavings` | A valid candidate did not reduce the rebuilt canonical request. |
| `EmergencyReduction` | The compatibility reducer shortened only older already-delivered result content. |
| `CapacityExceeded` | The request could not fit without reducing a group that had not yet been delivered exactly. |

Provider cache support and reporting vary. A successful rewrite increments a provider-neutral history generation, and compiled providers receive the complete rebuilt stateless request rather than reusing an incompatible opaque conversation identity. The unchanged stable prefix remains eligible for provider prefix caching. Threadsmith reports cache-read or cache-write tokens only when the provider supplies them; a missing counter is **unavailable**, not zero, and latency alone is never treated as proof of a cache hit.

Outside the active-turn line, `/context inspect` reports logical content tokens, estimated provider-wire input and budget, stable-prefix tokens, native/textual tool transport, the effective mode and source, completed-turn summary version/range, included or omitted messages and memory, retrieval rationale/provenance, stale or superseded exclusions, and exact pressure reductions. `/context compact` remains the separate completed-turn compaction command: malformed output, cancellation, provider failure, or persistence failure leaves that prior completed-turn snapshot active. Headless callers receive the same host-owned inspection projection as stable JSON.

Configure budgets under `context:conversation` in `.threadsmith/config.*`; `.threadsmith/config.example` documents recent-turn, summary, retrieval, pressure, artifact, and compaction bounds. Invalid values fail before model invocation. See [Conversation context operations](operations/conversation-context.md) for continuity defaults, failure behavior, retention, restoration, and headless contracts. See [Cache-optimized context operations](operations/cache-optimized-context.md) for request ordering, instruction confinement, diagnostics, and provider-acceleration safety.

## How repository changes are governed

Threadsmith treats the model as a reasoning component, not as the owner of control flow.

A typical change follows this sequence:

1. **Evidence collection** — authorized read-only tools gather bounded repository context. Tool availability and invocation policy determine what can be advertised or called.
2. **Plan proposal** — the model calls the host-owned `propose_plan` tool. Text that merely describes edits is not enough to enter mutation flow.
3. **Plan sanity and approval policy** — the host validates plan schema 2, runs cheap repository sanity checks over structured `fileIntents` (`Modify`, `Create`, `Delete`, `Move`, or `Rename`), classifies risk, and either returns repairable scope failures to the model for a bounded plan revision, prompts for manual review, or auto-approves according to `/plan-policy` / `planning:approvalPolicy`. Plan approval authorizes implementation work only, not repository writes.
4. **Implementation proposal** — after manual or policy plan approval, the TUI shows mutation-preview preparation status while the same run receives bounded phase-eligible read tools plus proposal-only `propose_mutations`. The model proposes typed text, supported semantic, or structured lifecycle mutations correlated to accepted plan steps.
5. **Proposal validation before Roslyn** — the host assigns identities and validates the proposal schema, plan-step ids, scope, paths, trust, current mutation baseline, baseline hashes, exact replacement text, lifecycle preconditions, structured-output size, and budgets. C# `RenameSymbol` proposals expand through the loaded semantic workspace when the model supplies the semantic symbol id and new identifier. Repairable host validation failures, such as wrong `expectedText`, are returned to the model for a bounded proposal retry; non-repairable policy/trust/path failures fail closed.
6. **Pre-mutation Roslyn screening** — before staging or user approval, proposed `.cs` changes are applied only to an in-memory overlay. Threadsmith parses the would-be source with Roslyn and, when a loaded project is available at sufficient semantic confidence, runs fast compilation diagnostics against the overlay without `dotnet build`. Blocking diagnostics are mapped to file/range, diagnostic id, message, changed line/hunk, containing syntax when available, and explicit omissions, then returned to the model for proposal-phase repair. Repository files remain unchanged. Trusted or isolated analyzer/code-style checks may participate when implemented and available; ordinary repository-supplied third-party analyzers and source generators are not loaded pre-approval and are reported as degraded omissions until post-approval validation.
7. **Private staging and exact diff** — only a proposal that passes host validation and cheap pre-mutation gates enters the private staged workspace. Threadsmith records the exact bounded diff. Interactive TUI presentation shows compact changed hunks with bounded unchanged context, hidden-line markers, and one blank display line after each hunk header without changing canonical diff content.
8. **Mutation approval policy** — depending on `/policy` and `mutation:approvalPolicy`, Threadsmith either prompts for approval or applies the host-authorized set automatically. Every policy preserves the exact diff and invariant guardrails.
9. **Authoritative pre-write baseline** — under the configured post-mutation validation stages, Threadsmith captures the exact affected pre-mutation diagnostic baseline before write-ahead mutation intent. When semantic-only validation is configured, this uses the loaded semantic workspace without launching a build; when compile/diagnostics stages are configured, affected projects are built to capture authoritative baseline diagnostics.
10. **Transactional application** — after authorization and baseline capture, the host records write-ahead intent, hash- and path-checks authorized files, applies atomic replacements/lifecycle operations, and reconciles the result before advancing.
11. **Post-mutation validation** — configured validation stages run in order. The default is semantic, compile, diagnostics, and affected tests. Build/test validation remains authoritative even if pre-mutation Roslyn screening passed.
12. **Correction or completion** — introduced compiler/test failures can stage a bounded correction against a promoted transactional baseline while retaining the original diagnostic baseline. Every correction repeats proposal validation, pre-mutation screening, exact diff, policy, transaction, and validation gates. The host records an authoritative final outcome rather than trusting model success claims.

Approval is fail-closed. The model cannot expose mutation tools to itself before the host has accepted the plan, and plan scope does not authorize destructive Git operations or writes outside approved repository roots.

### Pre- and post-mutation validation controls

Threadsmith has two validation layers around mutations:

| Layer | When it runs | What it does | How to enable or disable |
|---|---|---|---|
| Plan sanity checks | Before plan review or auto-approval | Checks structured plan `fileIntents` for repository-relative path safety, empty or ambiguous scope, modify/delete/move/rename sources that are missing, create/move/rename destinations that already exist, protected/secret/`.git` paths, generated/binary files, lifecycle/delete/move risk, dependency/configuration changes, and bounded scope size. Repairable failures are returned to the model for plan revision. | Always on when a plan is proposed. `execution:maxCorrectiveTurns` bounds repair attempts. `/plan-policy` controls approval after checks pass; it cannot disable checks. |
| Proposal/schema/path/baseline validation | Before staging | Validates typed mutation shape, plan-step correlation, scope, trust, repository-relative paths, baseline identities, exact replacement text, lifecycle preconditions, size limits, and budgets. | Always on. It cannot be disabled by repository configuration or approval policy. |
| Pre-mutation Roslyn syntax screening | Before staging and before approval | Parses proposed `.cs` overlay text in memory and returns blocking syntax diagnostics for model repair. It does not write files or run a build. | Automatic for proposed `.cs` mutations when the pre-mutation analyzer is available. There is no separate user/repository toggle. Non-C# changes skip this layer. |
| Pre-mutation Roslyn semantic/compilation screening | Before staging and before approval | Uses the loaded semantic workspace to run fast overlay compilation diagnostics without `dotnet build` when confidence permits. Unknown/orphan `.cs` files degrade to syntax-only analysis. | Enabled by having a loaded solution/project with semantic confidence. It degrades explicitly when the workspace is absent, stale, unloaded, or below required confidence. It is not controlled by `validation:stages`. |
| Pre-mutation analyzer/code-style screening | Before staging and before approval | May run only host-owned, allowlisted, trusted-policy-approved, or isolated analyzer/code-style checks. | Ordinary repository-supplied third-party analyzers/source generators are not loaded pre-approval. They degrade to omissions and remain covered by post-approval build/analyzer validation. |
| Exact diff and mutation approval | After staging, before writes | Shows/records the exact diff and applies `/policy` / `mutation:approvalPolicy`. | Select interactively with `/policy`; configure defaults with `mutation:approvalPolicy` and `mutation:largeDiffThreshold`. Invariant guardrails remain always on. |
| Pre-write diagnostic baseline | After approval, before writes | Captures the authoritative diagnostic baseline used to distinguish baseline from introduced failures. | Controlled by `validation:stages`; semantic-only avoids build, compile/diagnostics uses affected `dotnet build --no-restore`. |
| Post-mutation compile/diagnostic validation | After transactional apply | Builds affected projects and classifies/correlates diagnostics. | Include `compile` and/or `diagnostics` in `validation:stages`. Removing them narrows validation and is not the recommended default. |
| Post-mutation affected tests | After successful affected build/diagnostics | Selects and runs relevant tests with host-authored rationale. | Include `tests` in `validation:stages`; configure `validation:testScope` where supported. |
| Correction loop | After failed post-mutation validation | Lets the model propose a bounded correction; every correction repeats all proposal, pre-mutation, diff, policy, transaction, and post-validation gates. | Bounded by `execution:maxCorrectiveTurns`; cannot bypass approval, transaction, or validation. |

The default repository configuration leaves post-mutation validation broad:

```json
{
  "validation": {
    "stages": [ "semantic", "compile", "diagnostics", "tests" ],
    "ignoreBaselineDiagnostics": true,
    "testScope": "affected"
  }
}
```

`validation:stages` controls the authoritative post-approval validation gate, not the automatic pre-mutation Roslyn proposal screening. Stages run in order and a failing stage blocks later stages. Supported stage names are:

- `semantic` — fast in-process semantic diagnostics from the loaded workspace without launching a build;
- `compile` — affected project compilation through direct `dotnet build --no-restore`;
- `diagnostics` — normalized compiler diagnostic classification and mutation correlation;
- `tests` — affected test discovery and execution.

Semantic activity is visible in the interactive transcript as `SEMANTIC CHECKS` rows with elapsed time when duration display is enabled. Pre-mutation rows cover overlay syntax and compilation checks; semantic-only validation rows cover baseline and post-mutation diagnostics. Details are bounded host-authored summaries such as file/project counts, diagnostic counts, blocking diagnostics, omissions, and completion state. If post-apply validation is configured without the `semantic` stage, the TUI shows a `MUTATION: Validating applied mutation` lifecycle block so compile/diagnostics/test waits are not silent.

A repository may narrow `validation:stages`, for example to `[ "semantic" ]` for a fast local experiment, but doing so reduces the authoritative gate. Use this only when another trusted process supplies the missing build/test assurance. Pre-mutation screening remains active for `.cs` proposals where possible even when post-mutation stages are narrowed.

Mutation-related controls:

```json
{
  "planning": {
    "approvalPolicy": "reviewAll",
    "approvalRepositoryIdentity": null
  },
  "mutation": {
    "approvalPolicy": "reviewAll",
    "largeDiffThreshold": 500
  },
  "execution": {
    "maxCorrectiveTurns": 3,
    "maxModelRounds": 0,
    "maxPlanningToolRounds": 0,
    "maxStructuredOutputCharacters": 8388608
  },
  "formatting": {
    "style": "editorconfig",
    "applyOnMutation": true
  }
}
```

- `planning:approvalPolicy` sets the default plan approval behavior; `/plan-policy` persists every plan policy except `TrustSession` in repository settings, while `/plan-policy AlwaysTrustRepo` also requires both the repository marker and user-owned trust grant to match.
- `planning:approvalRepositoryIdentity` is a host-written repository marker for `AlwaysTrustRepo`; repository content alone cannot grant persistent plan trust without the matching user-owned plan-policy trust store.
- `mutation:approvalPolicy` sets exact-diff mutation approval behavior; `/policy` changes it for the running session and persists only the explicit `alwaysTrustRepo` opt-in.
- `mutation:largeDiffThreshold` controls when `ReviewRisky` treats an exact diff as large.
- `execution:maxCorrectiveTurns` bounds active-turn correction attempts for recoverable malformed or invalid model-authored requests, including malformed `propose_plan` arguments, invalid pre-execution tool batches, repairable plan revisions, mutation-proposal retries, and post-validation correction attempts.
- `execution:maxModelRounds` optionally bounds total model continuation rounds for a request, and `0` disables that separate cutoff; `execution:maxPlanningToolRounds` optionally bounds the initial planning rounds that advertise inspection tools before only `propose_plan` remains, and `0` disables that separate cutoff so exploration can use the full model-round budget; `execution:maxStructuredOutputCharacters` bounds mutation proposal output.
- `formatting:applyOnMutation` controls configured formatting around proposed mutations where formatting support is available; formatting does not bypass exact diff review.

### Plan approval policies

`/plan-policy` opens a numbered selector; `/plan-policy current` reports the active choice, `/plan-policy reset` returns to repository-persisted `ReviewAll` and revokes identity-fenced repository plan trust, and `/plan-policy <name>` selects directly:

| Policy | Behavior |
|---|---|
| `ReviewAll` | Default. Prompt for every valid sanity-checked plan. Persisted in repository settings when selected through `/plan-policy`. |
| `ReviewRisky` | Auto-approve low-risk valid plans; prompt for moderate or high risk. Persisted in repository settings when selected through `/plan-policy`. |
| `TrustSession` | Auto-approve low- and moderate-risk valid plans for the current process session. Session-only; does not rewrite repository settings. |
| `AlwaysTrustRepo` | Persistently auto-approve low- and moderate-risk valid plans for the exact repository identity. The host writes a repository marker plus user-owned trust grant; selecting another persisted policy or reset revokes the grant. |
| `AutoApproveAllValid` | Strongest explicit mode. Auto-approve every valid non-blocked plan after sanity checks, subject to repository trust and hard guardrails. Persisted in repository settings when selected through `/plan-policy`. |

Plan policy is distinct from mutation policy. Auto-approved plans still appear in the transcript as `PLAN: auto-approved`, remain durable structured contracts, and only allow the implementation proposal phase to start. They do not approve exact staged diffs, writes, process execution, validation results, commits, pushes, or external-system effects.

### Mutation approval policies

`/policy` opens a numbered selector; `/policy current` reports the active choice, and `/policy <name>` selects directly:

| Policy | Behavior |
|---|---|
| `ReviewAll` | Default. Prompt for every staged mutation set. |
| `ReviewRisky` | Auto-apply ordinary edits; prompt for moves, deletions, configuration or dependency changes, diffs over `mutation:largeDiffThreshold`, and invalid/outside-repository targets. |
| `TrustPlan` | Auto-apply only mutations contained by the accepted plan's declared files. Scope expansion still requires review. |
| `TrustSession` | Auto-apply valid in-repository mutations until the process session ends. |
| `AlwaysTrustRepo` | Auto-apply valid in-repository mutations and persist `mutation.approvalPolicy` in this repository. Selecting any other policy revokes persistent trust. |

Trust-based choices print a warning. Every policy still requires `TrustedMutation`, preserves the exact diff in events/projections, validates baseline hashes and approved roots, rejects prohibited/secret-bearing paths and `.git` metadata, runs configured validation, and never commits, pushes, resets, cleans, or otherwise performs destructive Git operations.

Optional Git-worktree isolation may be used by configured workflows, but Threadsmith does not treat Git as a transaction mechanism and does not perform destructive Git operations.

### Structured file lifecycle mutations

File creation, deletion, and movement are explicit versioned mutation operations, not ordinary tools or inferred shell commands. They are advertised only through `propose_mutations` during eligible implementation/correction phases and retain the same accepted-plan, exact-diff, approval, transaction, validation, and resume authority as text edits.

- **Create** requires an absent path and bounded text content. The proposal may select UTF-8 with or without BOM and LF or CRLF newlines.
- **Delete** requires the exact baseline SHA-256 and byte count. Baseline bytes remain privately available for compensation and rollback; deleted content is not logged.
- **Move** requires an exact source identity and an absent destination. Both endpoints must be declared by the accepted plan and worker assignment. A content descriptor makes move-plus-edit explicit; no namespace, project, or reference rewrite is inferred.
- **Case-only move** is represented explicitly and committed through the same private temporary-file transaction so case-insensitive filesystems do not lose or duplicate the file.
- **Project inclusion** is metadata only. Threadsmith never hides a project-file edit; an explicit project change must be part of the reviewed mutation set.

The aggregate preview always includes exact add/delete source and destination diffs and exposes lifecycle kind, risk, destination, and case-only status. `ReviewRisky` prompts for every move or delete and for project-system lifecycle changes. Commit detects the repository filesystem's casing behavior, removes baseline identities before publishing final identities, attempts every compensation cleanup/restore effect without honoring caller cancellation, and verifies final hashes before reporting `Applied`; incomplete compensation is aggregated and fails closed. Recovery distinguishes `NotStarted`, `Applied`, `Compensated`, `Conflicted`, and `Indeterminate`; ambiguous state fails closed rather than replaying the move. Rollback refuses to overwrite any file changed after commit.

Directory/glob operations, directory-tree deletion/movement, links, alternate streams, permissions, implicit project/namespace rewrites, overwrite moves, and Git staging/commit/move remain unsupported. Outside-root, traversal, `.git`, secret, prohibited, and reparse-point paths are denied under every policy.

### Cancellation, checkpoints, and resume

Execution writes versioned checkpoints at safe phase boundaries and write-ahead mutation-commit intent before repository effects. Cancellation before application leaves repository bytes unchanged. Interrupted builds/tests do not admit late output as authoritative. Explicit `ResumeRunCommand` uses the same host boundary for interactive and headless adapters. Resume fails closed for terminal runs, session/checkpoint identity mismatch, missing or corrupt continuation artifacts, unsupported schema, or an unresolved pending side effect; normal workspace baseline and external-change guards still apply before any later mutation commit. See [execution resumption operations](operations/execution-resumption.md).

### Parallel agents and isolated workers

Threadsmith can delegate one bounded child layer for parallel exploration, isolated implementation, and independent security, test, performance, and architecture review. Child agents are asynchronous runs inside the Threadsmith process; they are never separate agent executables. Existing Git, build, test, MCP, and authorized tool processes remain tracked infrastructure and do not host an agent.

Every child has a frozen role, objective, tasks, stopping condition, output schema, baseline, scope, model and reasoning selection, tool allow/deny set, trust ceiling, sensitivity, deadline, dependencies, and hierarchical budget. Read-only children cannot receive mutation tools or mutation trust. Children receive bounded governed evidence—not the parent or sibling transcript—and only schema-valid cited findings cross the join boundary.

Implementation workers require an approved Plan-37 plan and host-proven non-overlapping ownership. Ambiguous paths, directories, symbols, projects, generated outputs, solution files, central package/build configuration, or other shared surfaces fall back to serial execution. Each eligible worker gets a detached worktree under the host-managed temporary root and repeats the normal mutation proposal, exact-diff, approval, transaction, validation, correction, and cancellation gates there. A worktree isolates file state; it is not a security sandbox.

Worker results are frozen structured change sets, not branches to merge. Before selected changes enter the primary worktree, Threadsmith rejects incomplete or stale packages, out-of-scope paths, worker overlap, and changed parent baselines. The parent converts and restages selected changes through the existing transactional workspace, presents one fresh aggregate diff under the current mutation policy, and reruns aggregate affected builds/tests. Threadsmith does not merge, commit, rebase, cherry-pick, push, or resolve conflicts automatically.

Use `/agents <delegation-id>` to inspect the durable run tree. `/agents <delegation-id> cancel` requests hierarchical delegation cancellation; `/agents <delegation-id> cancel-child <assignment-id>` cancels one child and policy-declared dependents. The display contains stable IDs, phase, generation, terminal status, bounded usage, reason, and next legal action; raw child prose and hidden reasoning are not interleaved into the conversation. Headless callers use `StartDelegationCommand`, `GetDelegationCommand`, `CancelDelegationCommand`, and `CancelAgentAssignmentCommand` through the same dispatcher. Configure conservative admission limits under `agents` as documented in `.threadsmith/config.example`. See [parallel-agent operations](operations/parallel-agents.md).

## Tools and tool availability

### Availability versus invocation policy

Tool availability determines whether a tool is advertised to the model and resolvable for invocation. Invocation policy separately determines whether an available invocation is allowed, denied, or requires approval.

Repository availability uses:

```json
{
  "tools": {
    "enabled": [ "git_status", "find_references" ],
    "disabled": [ "find_implementations" ]
  }
}
```

When `enabled` is present, it is an allowlist for non-essential tools. `disabled` wins when an ID appears in both lists. Essential inspection and validation tools cannot be disabled.

Independent sibling tool calls may run concurrently after the complete model response is collected. The host derives confined resource claims from validated arguments and current repository/workspace/request state; the model cannot declare safety or choose a wave. Read/read compiled built-ins overlap only when their reviewed scheduling descriptor permits it. Approval, code/process, workflow, semantic-workspace, MCP, extension, unknown, and conflicting resource calls remain sequential. Configure `tools:parallel:enabled`, `maximumConcurrency` (1–16), and `failureMode`; see [Parallel tool execution](operations/parallel-tools.md) for metadata and scheduling details.

Use `/tools` to view enabled state, stable ID, category, source, and essential status. Selecting a non-essential entry persists the change immediately while preserving unrelated configuration. Persistence errors are reported in the terminal without ending the shell.

Loaded extension tools appear in the same catalog. Unloading an extension removes its active tools without deleting their saved preferences.

### Invocation policy

These settings narrow runtime use independently of availability:

- `tools:allow` and `tools:deny`;
- `tools:requireApproval`;
- `tools:allowedExecutables`;
- `tools:allowedNetworkHosts`.

`run_process` exposes a general `command` plus optional `timeoutSeconds`, executes through `tools:runProcess:shellExecutable`, and is available during ordinary conversation only when that bare shell name appears in `tools:allowedExecutables`. The shell runs in the repository root and supports its normal composition language, including pipelines. Allowing a shell therefore also permits nested commands launched by that shell; the allowlist governs the outer shell boundary, not tokens inside a command. Threadsmith resolves the shell only from absolute host `PATH` entries, never from the repository working directory. Cancellation terminates the complete tracked process tree. Process calls require approval by default and are withheld from ordinary conversation because that pipeline cannot prompt interactively. To advertise and permit unattended shell calls, set `tools:runProcess:requireApproval` to `false` in machine or user configuration. Repository configuration cannot grant that exemption, but it can reimpose approval by including `run_process` in `tools:requireApproval`; because ordinary conversation cannot prompt, that withholds the tool. Results retain bounded stdout and stderr.

### Built-in tools

The catalog includes repository listing/reading/search, typed local Git inspection, normalized .NET inventory, semantic symbol/reference/implementation discovery, controlled process execution, current date/time, and other host-governed capabilities. Recursive repository listing and text search skip prohibited/reparse-point descendants; installed releases include a RID-matched ripgrep executable and use it through a bounded `rg` fast path for whole-repository literal text searches, respecting repository ignore files while including relevant hidden files. Source-development launches prefer the same app-local `tools/rg(.exe)` layout and may use an `rg` found on `PATH` when no staged payload exists. Regex searches, narrowed globs, configured prohibited-path boundaries, unavailable ripgrep, or a failed native invocation use the confined managed scanner. Search prunes `.git`, `bin`, `obj`, SQLite databases (including `.threadsmith/threadsmith.db`), and oversized files as applicable; files that become locked, inaccessible, or unavailable are skipped without aborting the managed scan. Results and native output remain bounded. On Windows these tools also skip reserved DOS device-name entries such as `nul` so one unopenable path cannot abort the remaining inspection. Use `code_explore` when natural-language C# questions, exact C# symbols, stable symbol IDs, or repository-relative C# paths should return current source or safe current-context back-references, compiler-proven flow among named anchors, dispatch branches, or compact impact context; use granular semantic tools for exact follow-up; use `search` for exact text and regular expressions.

Plan 41 adds distinct `git_diff`, `git_log`, `git_show`, `git_blame`, and `git_compare_branches` tools. They accept closed modes and validated revision tokens, treat path filters as literal repository-relative data after `--`, preserve unusual filenames through NUL-delimited normalization, classify blobs before text decoding, disable pagers/color/external diff and text-conversion behavior, perform no remote access, and truthfully report bounded commits, paths, lines, patches, bytes, and execution time. `git_diff` supports working-tree, staged, root/ordinary commit, direct-range, and merge-base comparisons. Branch comparison reports its merge base, ahead/behind counts, and normalized changed paths. Git is evaluated by executable policy, and recursive evidence omits descendants outside approved roots or matching prohibited paths.

`dotnet_inventory` projects the authoritative selected loaded semantic workspace into deterministic solution/project, TFM, project-reference, package-reference, central-version-source, and test-project results. Caller-supplied path text cannot replace selected-solution provenance. It reports semantic confidence and omissions; degraded or absent workspace state is not presented as complete evaluation. Every selected solution, loaded project, and `Directory.Packages.props` metadata access is confined by approved-root, prohibited-path, and reparse-point policy before bounded reads; inventory never restores packages.

All Plan 41 tools require `TrustedRead`, use the central availability/invocation policy and evidence pipeline, and return the same normalized JSON through interactive and headless model turns.

Plan 42 adds `nuget_health`, `dotnet_build`, `dotnet_analyzers`, `dotnet_format_check`, `diagnostic_query`, `test_discover`, and `test_run_targeted`. These are distinct typed operations rather than argument routers. They accept only host-defined target, configuration, framework, limit, query, and identity fields; no arbitrary MSBuild property, logger, response file, adapter, runsettings, environment, command, or filter expression is accepted.

`nuget_health` reads bounded existing `obj/project.assets.json` data to distinguish direct and transitive resolved dependencies without restoring. Offline results report asset freshness, completeness, and omissions. Configured-source mode runs separate bounded vulnerable, deprecated, and outdated queries against HTTPS sources supplied only by trusted machine/user configuration; source hosts must also pass invocation network policy. Optional private sources pair a bounded source name and username with a logical `secrets:` reference. The exact reference must also be present in trusted `tools.allowedSecretReferences`; private-source credentials require `UserOwned` source trust, so repository values are ineligible. The value is resolved only at the final process boundary into the NuGet child environment; the generated temporary NuGet configuration contains source names/URIs but no credential and is deleted after use. Credentials never enter arguments, normalized results, or provenance. The tool never adds, removes, updates, restores, or writes package state.

`dotnet_build` and `dotnet_analyzers` require `TrustedBuild`, execute through the tracked process manager, use closed Debug/Release and validated TFM scopes, always pass `--no-restore`, and normalize diagnostics. `dotnet_format_check` uses `dotnet format --verify-no-changes --no-restore`; it reports drift but never applies formatting. Any formatting apply remains a Plan 10/30/37 transactional mutation with exact diff review.

`diagnostic_query` pages the bounded process-local exploratory index by invocation/run, project, file, code, severity, compiler/analyzer origin, and baseline class. `test_discover` enumerates one confined supported test project without restore/build, derives host-issued repository-bound identities, and can narrow results by exact namespace, class, method, or available trait metadata. `test_run_targeted` accepts only one unexpired issued identity, rechecks its project against current path policy, and generates an exact effective filter. Unknown, cross-repository, expired, or ambiguous identities fail closed.

Every Plan 42 result is labeled `Exploratory`. These tools cannot replace, overwrite, or satisfy M11 authoritative baseline, affected-project, test-selection, acceptance, or correction evidence. Process cancellation kills the tracked tree; output, dependencies, advisories, diagnostics, tests, time, and pagination are bounded. Interactive and headless turns use the same registry and normalized results.

#### `code_explore`: task-sufficient C# exploration

Use `code_explore` when a question is primarily about **how loaded C# code is structured or behaves** and the answer needs current source, semantic identity, flow, impact, or nearby prompt/configuration context. It is the high-level semantic exploration tool: it often replaces a manual sequence of `find_symbol` → `find_references`/`call_hierarchy` → `read_file` → `search` for ordinary code-understanding turns.

Good fits include:

- “How does this request reach the response builder?”
- “Show the source for this exact type, overload, or stable symbol ID.”
- “What declaration is at `src/Foo.cs` line 42?”
- “How do these two named methods connect?”
- “What callers/projects/tests look affected if this method changes?”
- “This C# method loads a prompt/config/resource; include the checked-in artifact that explains the behavior.”
- “I know the feature words but not the exact symbol names; find the likely compiler-known declarations.”

`code_explore` requires an opened repository at `TrustedBuild` with a semantic workspace loaded to at least partial compilation. Headless repository requests wait for semantic readiness before submitting a model request and fail closed instead of advertising unusable semantic tools when readiness is too low.

The model-facing schema is intentionally strict and minimal: required `query` plus optional `maxFiles`. Unknown fields are rejected. Users and models do not pass separate mode, path-anchor, artifact-anchor, traversal-depth, graph-size, source-limit, artifact-limit, timeout, digest, workspace-generation, or cursor fields; the host owns those controls.

Use the `query` text for:

- **Natural-language C# questions** — ordinary words are tokenized into bounded identifiers, qualified names, path-like spans, and ranking terms.
- **Exact C# symbols** — simple names, qualified names, overload-like signatures, or stable symbol IDs returned by earlier semantic tools.
- **Repository-relative `.cs` paths** — include the path, and optionally a line or line-range description, in the query text rather than as a separate argument.
- **Focused code terms** — feature words, type/member names, or nearby phrases when the exact symbol is unknown.
- **Host-issued continuation cursors** — paste the entire `code_explore:continue:...` value from a Markdown follow-up target as the next `query` to replay exact source, artifact, or impact continuation identity.

The host derives the internal exploration emphasis from the query and resolved anchors. Dependency, caller, affected-project, blast-radius, or test-impact wording derives the internal impact path so single-symbol questions such as “what depends on Foo?” return caller/project/test evidence without a model-visible `mode` field. Call-flow evidence appears when the resolved query supports a bounded compiler-proven path.

The default model-visible result is concise Markdown. It includes an exploration heading, relevant symbol/file count, blast-radius or call-flow evidence when present, grouped line-numbered current source or precise current-context back-references, associated artifacts when useful, bounded artifact completeness/omission notes, focused follow-up targets with pasteable retry query cursors when truncation occurs, and bounded omissions. Candidate-ranking tables, allocation summaries, adaptive-budget details, file/range SHA-256 digests, workspace generation values, emitted-range records, and other audit metadata remain in the authoritative structured result and are visible only through diagnostic/structured projections.

Natural-language discovery is deterministic and Roslyn-backed. Query text is inert data: it is never executed, provider-reranked, embedded, or treated as an unbounded repository text search. Ranking favors exact/pinned evidence, qualified names, distinctive identifiers, multi-term/co-located structure, graph connectivity, and explicit generated/test focus over isolated common-word collisions. If the result is ambiguous or incomplete, it reports alternatives, omissions, and continuation targets rather than silently guessing.

`code_explore` can also return **associated non-C# artifacts** as a separate supplement to the C# semantic spine. Supported associations are derived by the host from current C# evidence, host-issued artifact continuation cursors, Roslyn additional documents, analyzer configuration documents, loaded project metadata files, bounded textual project item/resource references, source-literal repository-relative paths, logical prompt/configuration names, and bounded exact-name matches under selected source/project directories. Markdown labels returned artifacts with their relationship, evidence strength, origin C# source, content excerpt when available, and omission/completeness notes when content is absent or truncated; the authoritative structured result also carries media kind, current digest, line range, completeness, and replay metadata. Raw project-file item text is marked as weaker textual inference when item conditions/imports/removes have not been evaluated. Prompt templates, JSON/YAML configuration, XML/resx/project metadata, Markdown, schemas, and text templates remain untrusted repository data: Threadsmith never executes, evaluates, imports, renders, expands external entities, or grants authority from their contents.

Source and artifact output have independent host-owned limits and may also be clamped by the selected model budget. Overflow is reported through omissions and Markdown follow-up targets; paste a `code_explore:continue:...` retry cursor as the next `query` to replay exact host-owned continuation state. Artifacts are omitted when they are binary-shaped, malformed, oversized, missing, changed during read, outside approved roots, prohibited, reparse/device paths, secret/credential-shaped, Git metadata, build output, generated transient output, unsupported media, or unavailable through bounded inventory. Exact-name artifact lookup uses declared, policy-allowlisted host-owned Git inventory when available, parses NUL-framed records before sanitization, caches repeated directory inventories within the query, and fails closed if inventory cannot be trusted.

For overlapping follow-ups, unchanged complete C# source ranges may be replaced with compact back-references only when the host proves the exact range is still present verbatim in the current canonical model request for the same repository, workspace, generation, path, and digest, and only when the serialized pointer is smaller than the source it replaces. Edits, compaction, different sessions/repositories, short spans, partial output, pointer-larger-than-source cases, policy denial, or uncertainty cause safe re-emission or omission. Artifact range deduplication is intentionally separate and conservative.

Use the granular tools when they are the better fit: `find_symbol` for exact symbol lists, `find_references` or `find_implementations` for focused follow-up, `call_hierarchy` or `symbol_impact` for a standalone graph query, `generated_code_query` for generated-document inventory, and `search`/`read_file` for exact text or non-C# files not related by `code_explore` evidence.

Plan 43 adds `call_hierarchy`, `symbol_impact`, `csharp_pattern_search`, and `generated_code_query`. All four require an opened `TrustedBuild` semantic workspace, are read-only, run no process/network/build/restore/generator/mutation operation, and execute against one captured workspace generation. If invalidation or reload changes that generation before completion, the late result is discarded rather than projected as current.

`call_hierarchy` accepts a stable symbol ID, optional incoming/outgoing/both direction, and one optional depth hint. Node counts, edge counts, timeouts, and all other traversal limits are host-owned. The default model-visible projection is a compact call list: caller, callee, source call site, direct/static/constructor/interface/virtual/extension/local-function/delegate/unknown dispatch, ambiguity, cycle closure, and bounded omissions. The richer structured result remains host-owned audit data. Dynamic, reflection, and runtime-only targets are omissions; results never claim whole-program completeness.

`symbol_impact` accepts only a stable symbol ID. Traversal depth, node counts, edge counts, and timeouts are host-owned. The default model-visible projection is a deterministic ranked impact list over loaded references, callers, implementations/overrides, dependent projects and test projects, plus generated/linked source classification, with compact reasons and omissions. Runtime effects and diagnostics not present in the loaded snapshot are explicitly not inferred, so impact is planning evidence rather than proof or mutation authorization.

`csharp_pattern_search` uses a flat inert model schema: required shape kind plus optional exact name, containing type, repository-relative path, closed C# modifiers, and exact attribute names. Supported shapes are declaration, type, method, property, field, attribute, invocation, object creation, and member access. Version fields, nested pattern wrappers, capture names, result limits, timeouts, arbitrary source snippets, regex, scripts, callbacks, analyzers, assemblies, and executable predicates are not model-facing schema fields; unsupported modifiers and malformed or oversized names fail closed. The compact model-visible projection lists bounded file/range matches and omissions, while confidence, workspace generation, and richer structured fields remain host-owned audit data.

`generated_code_query` inventories only documents already classified in the loaded workspace through `.g.cs`/`.generated.cs`/`obj` convention or Roslyn source-generator exposure. It reports project, path/name, linked classification, explicit origin (`FileConvention`, `SourceGenerator`, `CompilerOrSdk`, or `Unknown`), and optional bounded source content. Missing generator identity is unknown rather than guessed; document/content limits disclose truncation, and the query never runs a generator implicitly.

All graph and search results carry semantic confidence, workspace generation, provenance, completeness, and omissions. The same tool definitions and normalized JSON are available to interactive and headless model turns.

`datetime` is enabled by default and returns round-trip UTC/local timestamps, local timezone ID, and effective offset.

`csharp_script` is disabled by default and requires `FullyTrustedAutomation`. It executes each request in a fresh tracked worker process with bounded standard input, output, and time. It rejects directives and file, network, process, environment, reflection, native, dynamic, unsafe, and non-allowlisted namespace access.

These restrictions are defense in depth, not an operating-system sandbox. Enable scripting only for repositories you trust.

Configure it with scalar values:

```json
{
  "tools": {
    "config": {
      "csharp_script": {
        "timeout_ms": "5000",
        "max_output_bytes": "65536",
        "allowed_assemblies": "System.Linq,System.Collections,System.Collections.Generic"
      }
    }
  }
}
```

### Governed web search

`web_search` searches an external index through the compiled Brave Search adapter. It is read-only with respect to the repository, but each invocation sends the query text to an external service. For that reason, the tool is disabled by default and has an additional consent gate beyond ordinary tool availability, repository trust, and invocation policy.

#### Enable, consent, and revoke

In the interactive terminal:

1. Open the repository for which search should be available.
2. Run `/tools` and select **Web Search**.
3. Read the outbound-disclosure prompt and choose **Yes — grant consent and enable**.

Consent is bound to the canonical path of the active repository and stored in user-owned local application data at `Threadsmith/outbound-consent.json`, outside the repository. The stored record contains a hash of the canonical path, not the path or any search query. A checked-in configuration file, repository trust grant, prompt, model call, or extension cannot create consent. Opening the same content through a different canonical path therefore requires a fresh confirmation.

Selecting **Web Search** again in `/tools` disables it and immediately revokes consent for that repository. Missing, malformed, unknown-version, or path-mismatched consent fails closed. A repository may request that the tool be enabled in configuration, but `/tools` continues to report **consent required** and the model cannot see or invoke it until the user confirms.

#### Request and result bounds

The model supplies a plain-text query of at most 500 characters, a result count from 1 through 20 (default 5), an optional BCP-47 language/region hint such as `en` or `en-US`, and an optional freshness window from 1 through 365 days. Empty queries, control characters, invalid bounds, and queries detected as containing credentials or other sensitive data are rejected before network access. Rejected raw queries are not retained.

Threadsmith does not fetch result pages, crawl sites, submit forms, manage cookies, or provide authenticated browsing. It accepts only normalized HTTPS result URLs and returns bounded titles, snippets, rank, provider ID, retrieval time, and query provenance. Markup and control characters are removed. Titles and snippets enter model context as **untrusted external evidence**: they cannot override host policy, grant approval, invoke another tool, or authorize a repository change.

#### Configuration locations

Credential-bearing web-search provider settings and the network/secret allowlists come only from the repository-excluding trusted view:

- machine-wide: `%ProgramData%/Threadsmith/config.json`;
- user-wide: `~/.threadsmith/config.json`;
- exact `THREADSMITH_` environment variables for ordinary configuration keys.

Repository and session configuration may narrow tool availability and policy, but cannot replace the Brave endpoint/reference, grant its network or secret allowlists, or create outbound consent. `--set:` participates in ordinary effective configuration but is intentionally excluded from this credential-bearing trusted view. Never put the Brave API key in any ordinary configuration file or command argument; provide it through the separate static-secret resolver described below.

A complete provider and tool-policy example is:

```json
{
  "tools": {
    "enabled": [ "web_search" ],
    "allow": [ "web_search" ],
    "allowedNetworkHosts": [ "api.search.brave.com" ],
    "allowedSecretReferences": [ "secrets:BRAVE_SEARCH_API_KEY" ]
  },
  "webSearch": {
    "provider": {
      "id": "brave",
      "kind": "brave",
      "endpoint": "https://api.search.brave.com/res/v1/web/search",
      "secretReference": "secrets:BRAVE_SEARCH_API_KEY",
      "timeoutSeconds": 15,
      "maximumResponseBytes": 1048576,
      "retryLimit": 1,
      "minimumRequestIntervalMilliseconds": 200
    }
  }
}
```

If `tools.enabled` is present, it is an allowlist for non-essential tools, so include every other non-essential tool that should remain available. `tools.disabled` wins over `tools.enabled`. The configured endpoint host must also appear in `tools.allowedNetworkHosts`, the logical credential name must appear in `tools.allowedSecretReferences`, and `tools.allow` must permit `web_search`; these policy settings still do not replace explicit consent.

Provider settings and accepted bounds are:

| Key | Default | Accepted values |
|---|---:|---|
| `webSearch:provider:id` | `brave` | Stable provider label used in provenance. |
| `webSearch:provider:kind` | `brave` | `brave` only; other provider kinds are not compiled into this host. |
| `webSearch:provider:endpoint` | `https://api.search.brave.com/res/v1/web/search` | Absolute HTTPS URL without embedded credentials. Redirects must remain on the configured HTTPS origin. |
| `webSearch:provider:secretReference` | `secrets:BRAVE_SEARCH_API_KEY` | Logical name beginning with `secrets:`. |
| `webSearch:provider:timeoutSeconds` | `15` | 1–60 seconds for the complete operation. |
| `webSearch:provider:maximumResponseBytes` | `1048576` | 1,024–4,194,304 bytes. |
| `webSearch:provider:retryLimit` | `1` | 0–2 bounded transient retries. |
| `webSearch:provider:minimumRequestIntervalMilliseconds` | `200` | 0–60,000 ms between requests in the current process. |

### Governed web fetch

`web_fetch` is a separate, default-disabled public-HTTPS textual retrieval capability. It remains absent from unrelated model requests and becomes visible only for an eligible search reference, explicit exact grant, or exact URL recognized from the fresh current user request. Repository configuration cannot grant consent or URL authority.

Consent schema 3 explains that Threadsmith may send search terms, retrieve selected results, contact an exact public HTTPS URL in the current request only when the model invokes fetch, and supply untrusted fetched content to the model. Schema 2 remains valid for existing search-result and `/fetch-authorize` behavior but does not enable fresh-message URL inference; the first eligible turn offers visible re-consent and denial continues without network traffic.

After consent, `Read https://example.com/docs` can produce a one-shot opaque `userUrlId` without a separate command. Only the newly submitted raw top-level message is scanned, with bounded deterministic recognition and no DNS/network activity. Candidates must begin the message or follow a supported opening/token delimiter; embedded substrings such as `prefixhttps://...` are not URLs for this authority route. A URL span reaching the 32-KiB scan boundary is rejected unless the raw message ends there; Threadsmith never authorizes its truncated prefix. Authority is exact, message/repository/session/run/generation/expiry-bound, non-restorable, and revoked at the next turn or lifecycle/policy boundary. Prior conversation, memory, repository text, prompts, model/tool output, fetched pages, extensions, MCP, and hooks cannot mint these references.

If the model proposes a different `url` while fetch is already active, interactive mode shows a host-owned `Deny`/`Approve one attempt` prompt containing model provenance, sanitized origin, a conservatively redacted path shape, query presence, and a digest—never path tokens or query values. Prompt details and URL-free lifecycle notifications remain process-local and are not written to session history, projections, telemetry, hooks, or restoration. Approval applies only to that pending invocation and never to a redirect, retry, sibling, origin, session, or later run. Headless mode never prompts and reports `DirectAuthorizationRequired` with the sanitized origin, redacted path shape, and exact digest needed to identify the destination; automation can create an exact grant and retry. When a headless session is reused, only tool activity from the current run is printed.

`/fetch-authorize <initial-public-https-url> [redirect-public-https-url ...]` and headless `AuthorizeWebFetch`/`AuthorizeWebFetchChain` remain the advance-authorization and exact redirect-chain surfaces. Search-result retrieval permits only bounded same-origin redirects. Current-message and inline-approved routes authorize only their initial URL. Every destination is DNS/address validated and connection-pinned; local, private, metadata, reserved, mixed, and rebound targets fail closed. Cookies, ambient credentials/proxies, authentication, active content, subresources, binary/PDF content, and automatic redirects are disabled. Returned HTML/plain/Markdown/JSON is bounded readable text framed as untrusted evidence, with query-free provenance and content digests. See [Governed web fetch operations](operations/web-fetch.md).

| Key | Default | Meaning |
|---|---:|---|
| `webFetch:maximumUrlCharacters` | `2048` | URL bound; compiled hard maximum 8,192. |
| `webFetch:maximumRedirects` | `3` | Manual redirect bound; compiled hard maximum 5. |
| `webFetch:timeoutSeconds` | `15` | Whole-operation deadline; compiled hard maximum 60 seconds. |
| `webFetch:maximumCompressedBytes` | `1048576` | Wire-body bound; compiled hard maximum 4 MiB. |
| `webFetch:maximumDecodedBytes` | `2097152` | Decompressed source bound; compiled hard maximum 8 MiB. |
| `webFetch:maximumExtractedCharacters` | `131072` | Readable-text bound; compiled hard maximum 512 KiB. |

Invalid provider kinds, insecure or credential-bearing endpoints, non-secret credential references, and out-of-range limits fail during configuration. Retries apply only to bounded transient failures, honor the overall timeout and cancellation, and never allow a redirect to another origin.

#### Supply the Brave API key

Create or sign in to a Brave Search API account and generate a subscription key in the [Brave Search API dashboard](https://api.search.brave.com/app/keys). Review the plan and pricing presented by Brave before subscribing because API availability, included credits, and usage charges are provider-controlled and may change.

The default logical reference is `secrets:BRAVE_SEARCH_API_KEY`. The recommended durable source is the owner-protected user store at `~/.threadsmith/secrets/config.json`:

```json
{
  "secrets": {
    "BRAVE_SEARCH_API_KEY": "<your-Brave-Search-API-key>"
  }
}
```

An exact environment variable may override the user store for one process. PowerShell uses double underscores to represent reference separators:

```powershell
$env:THREADSMITH_secrets__BRAVE_SEARCH_API_KEY = "<your-Brave-Search-API-key>"
dotnet run --project src\Threadsmith.App -- --tui
```

On Bash-compatible shells:

```bash
THREADSMITH_secrets__BRAVE_SEARCH_API_KEY='<your-Brave-Search-API-key>' \
  dotnet run --project src/Threadsmith.App -- --tui
```

Brave requires `UserOwned` source trust, so the lower-trust repository store is ineligible even when ignored and untracked. If `secretReference` is changed, add that exact logical reference to `tools.allowedSecretReferences` and use the corresponding nested user-store path or environment key. For example, `secrets:search:brave` is stored under `secrets` → `search` → `brave` and maps to `THREADSMITH_secrets__search__brave`. Credentials are resolved only at the transport boundary and are not exposed to the model, recorded in consent, or included in result provenance.

## Model providers, secrets, and reasoning

Threadsmith loads model providers from two dedicated catalogs:

1. `~/.threadsmith/providers.json` — optional user-level base catalog;
2. `<repository>/.threadsmith/providers.json` — optional repository overrides.

Providers and their nested models merge by stable `id`, not array position. Matching entries inherit omitted settings, other arrays replace the inherited array, and new entries append in repository order. Set `enabled` to `false` to disable an inherited provider or model. An override cannot change an inherited entry's `type` discriminator. If an inherited provider has a secret reference, a repository override also cannot change its provider-specific connection or authentication settings, including `baseUri` and `secretKeyReference`.

Each provider owns an array of typed models. `defaultProviderId` is a case-insensitive stable provider string; `defaultModelId` is the model's stable GUID. See [`.threadsmith/providers.example.json`](../.threadsmith/providers.example.json) for a complete secret-free example.

```json
{
  "schemaVersion": 1,
  "defaultProviderId": "local-openai",
  "defaultModelId": "4d36e96e-292b-4c25-bb63-2f63821d5729",
  "providers": [
    {
      "type": "openai-compatible",
      "id": "local-openai",
      "name": "Local OpenAI-compatible",
      "baseUri": "http://127.0.0.1:1234/v1/",
      "chatCompletionsPath": "chat/completions",
      "headers": { "X-Client-Name": "Threadsmith.NET" },
      "secretKeyReference": "secrets:models:local-openai",
      "models": [
        {
          "type": "openai-compatible",
          "id": "4d36e96e-292b-4c25-bb63-2f63821d5729",
          "name": "Local coding model",
          "modelId": "local-model",
          "contextWindow": 32768,
          "maximumOutputTokens": 4096,
          "capabilities": { "streaming": true, "toolCalls": true, "structuredOutput": true },
          "supportedReasoningLevels": [ "none", "medium" ],
          "defaultReasoningLevel": "medium"
        }
      ]
    }
  ]
}
```

Only compiled, explicitly registered discriminators are accepted. Unknown types, case-insensitive duplicate properties/IDs, invalid or disabled defaults, excessive input, inline credential fields, and non-`secrets:` references fail before model activation. OpenAI-compatible `baseUri` paths are preserved whether or not they end in `/`; `chatCompletionsPath` must be a bounded relative path beneath that base. Optional request headers are bounded and cannot contain authorization, proxy authorization, cookies, API-key-like names, hop-by-hop fields, or control characters. Authentication and allowed configured headers are applied to each request rather than `HttpClient` defaults.

### OpenAI Codex authentication and discovery

Threadsmith's `openai-codex` provider uses native Responses and a Threadsmith-owned OAuth grant. It never reads Pi credentials, settings, model catalogs, or runtime state. Authenticate with `threadsmith --codex-login` (headless device flow), `threadsmith --tui --codex-login` (browser PKCE/localhost callback), or the equivalent `threadsmith [--tui] /auth openai-codex [login|status|logout]` form. Use `--codex-status` and `--codex-logout` for bounded status and removal.

After login, Threadsmith queries the protected Codex `/models` resource and projects every distinct model returned for that account. There is no hard-coded Codex model list. Stable Threadsmith profile IDs are derived deterministically from provider/model identity; returned context and reasoning metadata is bounded and normalized. Credential-free model metadata is cached under the user-owned `~/.threadsmith` directory and becomes selectable at the next process start. Logout removes both Threadsmith's Codex grant and this metadata cache. Repository provider catalogs cannot replace the host-owned `openai-codex` definition.

Codex authorization/resource authorities, client identity, scopes, redirect URI, and credential headers are compiled policy. Browser login requires `http://localhost:1455/auth/callback`; if that port is occupied, stop the conflicting process and retry. Headless login displays an OpenAI verification URI and one-time user code. Authentication failures never fall back silently to another provider.

### Legacy model-profile migration

Legacy `model:profiles[]` remains available for a bounded compatibility period when neither dedicated catalog exists. Threadsmith adapts those profiles only in memory, preserves each profile GUID and exact endpoint/request settings, writes no configuration, and emits one deprecation warning per startup. The removal milestone is not yet selected and requires a later announced decision.

If a dedicated provider catalog and any legacy profile are both present, startup fails with an ambiguity error. Migrate mechanically by moving the legacy endpoint's authority/base path to provider-level `baseUri`, keeping `chat/completions` as `chatCompletionsPath`, moving `secretKeyReference` to the provider, retaining the legacy profile GUID as the nested model `id`, and copying model ID, capability, cost, workload, sensitivity, reasoning, temperature, timeout, and retry settings to that model. Do not place the resolved credential or an authorization header in `providers.json`.

### Model HTTP transport

The shared application-lifetime connection pool uses normal layered configuration under `model:http`: `pooledConnectionLifetimeSeconds` (default 900, range 60–86400), `pooledConnectionIdleTimeoutSeconds` (default 120, range 10–3600), `connectTimeoutSeconds` (default 30, range 1–300), and `maxConnectionsPerServer` (default 16, range 1–1024). Invalid values fail startup. Cookies remain disabled to prevent cross-provider state, and the global `HttpClient` timeout remains disabled because each model's `timeoutSeconds` owns the complete request deadline.

Profile selection continues to check capability, context window, cost ceiling, sensitive-data policy, and workload compatibility. A compatible configured default wins before advisory or least-cost alternatives.

Context assembly is model-specific. For each request, Threadsmith uses the selected profile's `contextWindow` and subtracts its effective `requestOutputTokenReserve`; when omitted by a legacy catalog the reserve defaults to `maximumOutputTokens`. The provider maximum is a separate hard capability and may equal the context window when an explicit smaller reserve leaves positive input capacity. The remaining capacity is available for governed input. There is no shared `context:maxTokensPerRequest` ceiling across models. The composer status denominator reports the selected model's full configured context window, while context inspection records the smaller effective input budget after the output reserve. Switching models changes both values at the next request boundary.

### Repository model selection

Use `/models` to open the keyboard selector. Choices show provider/model identity, context/output limits, reasoning capability, and the current marker. The selected provider id, stable profile GUID, and effective reasoning level are written together to `.threadsmith/config.json`; unrelated settings are preserved.

Repository selection wins over `defaultProviderId`/`defaultModelId` in the user provider catalog. Those defaults apply only when both repository selection ids are absent. Partial, malformed, mismatched, missing, or disabled repository intent fails closed with repair guidance rather than silently selecting a user default.

A switch changes provider routing for the next request. In-flight work retains its captured model. An exact supported reasoning level is preserved; otherwise reasoning becomes `none` and the terminal lists valid `/reasoning` choices. `/reasoning` changes persist to the same repository selection. Opening another repository reloads its selection (or the user default when no repository selection exists) and redirects later `/models` and `/reasoning` writes to that repository. Current-context occupancy is cleared until the next request is assembled for the new context limit; cumulative provider token usage is not reset.

The TUI and headless adapter use the same list, current-selection, select-model, and set-reasoning host commands. Headless callers therefore observe the same validation, reset, persistence, and repository-rebinding behavior.

### Secrets

Supported static-credential fields contain logical `secrets:` references, never values. Threadsmith retains the reference and resolves it through one host-owned boundary only when the owning model, Brave, MCP, trusted hook, or private NuGet operation needs to authenticate. Ordinary strings, configuration inspection, prompts, tools, and model output never trigger discovery.

The recommended durable personal store is the strict-JSON file `~/.threadsmith/secrets/config.json`:

```json
{
  "secrets": {
    "BRAVE_SEARCH_API_KEY": "<credential>",
    "models": {
      "example": "<credential>"
    }
  }
}
```

Reference segments map directly to nested properties below the root `secrets` object. Empty placeholder values are rejected, so a checked-in example or newly prepared file fails closed until real values are supplied.

The optional repository-local store uses the same nested format at `.threadsmith/secrets/config.json`. It is lower trust and is accepted only when the exact confined file is untracked/not staged **and** covered by an effective Git ignore rule. Threadsmith checks both conditions before reading it and fails closed when Git state is unavailable or indeterminate. Add `.threadsmith/secrets/` to `.gitignore`; ignore is protection against accidental commits, not encryption. Rotate any value that was ever committed.

For an eligible request, fixed host-owned precedence is:

1. the exact environment variable;
2. the active repository store;
3. the user store.

Repository values cannot satisfy credentials requiring user-owned or managed authority. Current trust requirements are:

| Consumer | Secret requirement | Minimum source trust |
|---|---|---|
| OpenAI-compatible configured model | Optional `secretKeyReference` | `RepositoryOwned` |
| Brave `web_search` | Required API-key reference | `UserOwned` |
| MCP stdio scope, static HTTP headers, and optional OAuth client secret | Conditional profile references | `UserOwned` |
| Managed HTTP lifecycle hook | Conditional single bearer reference | `UserOwned` |
| Private configured NuGet advisory source | Conditional source credential | `UserOwned` |
| Native Codex and MCP OAuth access/refresh tokens | Lifecycle-managed caches, not static providers | User-owned specialized cache |

Repository configuration cannot add/reorder providers or weaken source trust. For `secrets:models:example`, the compatible variable is:

```powershell
$env:THREADSMITH_secrets__models__example = "<credential>"
```

Environment variables are optional rather than mandatory. The user store is edited explicitly outside Threadsmith; no command accepts a secret in normal command arguments. Owner-only modes are required on Unix, and Windows users should retain the user-profile ACL. MCP and Codex access/refresh-token caches remain lifecycle-specific and must not be copied into these static stores.

Failures report only the logical reference, component, safe attempted/skipped sources, stable classification, and remediation. Values, store/environment contents, and raw provider exceptions never enter model context, status, logs, events, persistence, diagnostics, hooks, or support bundles. See [static secret discovery](operations/secret-discovery.md) for setup, Git remediation, trust rules, and troubleshooting.

### Reasoning levels

Models declare `supportedReasoningLevels` from `none`, `minimal`, `low`, `medium`, and `high`, plus a `defaultReasoningLevel`. OpenAI-compatible models may opt into versioned closed `reasoningCompatibility` modes for standard/mapped effort, compiled chat-template or fixed additions, always-on reasoning, or unsupported reasoning. `/reasoning` distinguishes selectable, always-on, and unsupported models; level changes are accepted only when selectable and advertised. Switching models resets the shared session level to `none`. Hidden reasoning is transient-only; migration 7 purges historical reasoning-event rows and new reasoning text is excluded from durable events, conversation, memory, hooks, telemetry, evidence, and diagnostics. See [model-provider operations](operations/model-providers.md).

For full provider fields, retry, timeout, usage, and cost behavior, see [operations/model-providers.md](operations/model-providers.md). Transient DNS and connection failures use the selected profile's bounded retry policy. Ordinary conversation is not charged to an execution-token budget; cumulative session usage shown in the TUI is telemetry, not a quota. Mutation-proposal operations receive fresh configured budget scopes.

## Repository configuration

Repository configuration lives at `.threadsmith/config.json`. It is data, not executable code. The complete annotated schema is [.threadsmith/config.example](../.threadsmith/config.example).

Important sections include:

| Section | Purpose |
|---|---|
| `solution.path` | Remembered solution/project selection. |
| `model` | Provider profiles and defaults. |
| `tools` | Availability, invocation policy, allowlists, and per-tool scalar configuration. |
| `mutation` | Approval policy and the `ReviewRisky` large-diff threshold. |
| `context.conversation` | Default mode plus recent-turn, summary, retrieval, pressure, artifact, and compaction bounds. |
| `repository` | Editable roots, prohibited paths, and lifecycle policy. |
| `tui` | Theme and session-status configuration. |
| `extensions` / `.threadsmith/extensions.json` | Extension runtime selection and settings. |
| prompt append files | Ordered repository-provided model context. |

Threadsmith combines compiled, machine, user, repository, session, CLI, and ordinary environment layers using Microsoft.Extensions.Configuration semantics. Static secret stores and the normalized `THREADSMITH_secrets__...` environment subtree are deliberately excluded from that graph and are consulted only by `ISecretResolver` at an explicit privileged boundary. Malformed JSON stops startup with exit code `2` and a concise standard-error message identifying the affected file and parser location rather than an unhandled stack trace.

Repository JSON reads accept comments and trailing commas. Solution, model/reasoning, tool, and mutation-policy preference updates serialize their complete read-modify-replace operations through one repository settings coordinator, preserving unrelated keys and concurrent changes.

### Repository instruction bundles and prompt append files

For each turn, Threadsmith builds one repository instruction bundle from:

1. applicable `AGENTS.md` files from the repository root toward the active working scope, in parent-to-child order; then
2. configured prompt append files, conventionally under `.threadsmith/prompts/`, in configured order and with distinct provenance.

This means a nested `AGENTS.md` applies only when its directory contains the active scope; a sibling instruction file does not apply. A missing parent file does not prevent a deeper applicable file from being discovered. All repository-provided instructions are untrusted input: they cannot override host policy, approval rules, tool policy, trust, or coding guardrails.

The ordered bundle is content-addressed and revalidated at every turn boundary. An applicable instruction change therefore takes effect on the next request even if a filesystem watcher notification is lost. Changing an ordinary source file does not change the instruction bundle or its stable identity.

Instruction and append paths must remain inside the repository, avoid prohibited paths and symbolic-link/junction/reparse traversal, decode as strict UTF-8, remain unchanged during their bounded snapshot read, and satisfy configured per-file, total-size, count, and depth limits. Unsafe or changing sources fail closed before model invocation.

## Themes and session status

The default `system` theme inherits native terminal foreground/background. Other built-ins are:

- `forge-dark`;
- `ocean`;
- `high-contrast`.

Use `/theme`, `/theme <id>`, or `/theme current`. A selection affects future output and atomically updates only `tui.defaultTheme` in `~/.threadsmith/config.json`, preserving unrelated settings, comments, trailing commas, and surrounding formatting. Normal configuration precedence still applies, so a higher-precedence repository, session, CLI, or environment value may override the user default at startup. Theme changes do not rewrite prior scrollback or create domain events.

Configured themes use semantic roles rather than fixed screen coordinates. A configured theme's `styles` object may contain these role names:

| Role | Styled content |
|---|---|
| `Default` | Ordinary model/transcript text and the fallback style for unspecified roles. |
| `Brand` | Startup branding and identity. |
| `Muted` | Secondary or de-emphasized information. |
| `Status` | General host status messages. |
| `SessionStatus` | Composer-adjacent repository, model, context, and token status. |
| `Hyperlink` | Validated clickable links. |
| `ToolSuccess` | Successful tool completion. |
| `ToolFailure` | Failed tool completion. |
| `SelectionPrompt` | Selector prompts. |
| `SelectionItem` | Unselected selector entries. |
| `SelectionHighlight` | The currently selected entry. |
| `Success` | General successful outcomes. |
| `Warning` | Warnings. |
| `Error` | Errors and failures. |
| `UserPrompt` | User-authored transcript content when projected by a surface; ordinary composer submissions are not redundantly echoed. |
| `ComposerPrompt` | The interactive repository-name composer prompt. |
| `ThinkingIndicator` | The transient `THINKING` indicator. |
| `Reasoning` | Streaming reasoning enabled with `/thinking` or `Ctrl+T`. |
| `DiffAdded` | Added diff lines. |
| `DiffRemoved` | Removed diff lines. |
| `DiffContext` | Neutral/context diff lines, including hunk/file headers and display-only hunk spacing. |

Each role accepts optional `foreground` and `background` colors plus the Boolean decorations `bold`, `dim`, `italic`, `underline`, `strikethrough`, and `invert`. An omitted or `null` color inherits. Unspecified role colors fall back through `Default`, then the built-in `system` theme, whose ordinary foreground and background inherit from the terminal. Stable system semantic decorations—such as bold headings—remain active unless that exact role explicitly overrides its decorations.

Colors accept a case-insensitive named value or an exact six-digit RGB value in `#RRGGBB` form. The complete named-color allow-list is:

| Standard | Bright |
|---|---|
| `black` | `brightblack` |
| `red` | `brightred` |
| `green` | `brightgreen` |
| `yellow` | `brightyellow` |
| `blue` | `brightblue` |
| `magenta` | `brightmagenta` |
| `cyan` | `brightcyan` |
| `white` | `brightwhite` |
| `grey` | — |

`gray`, three-digit hex, eight-digit hex, alpha values, CSS color names, and terminal escape sequences are not accepted. Named colors are normalized to lowercase and RGB values to uppercase. Theme ids may contain only ASCII letters, digits, `-`, and `_`. Invalid colors, ids, control characters, ANSI/OSC content, unknown roles/settings, and excessive values invalidate only the affected theme; valid configured siblings remain available. Interactive startup prints a sanitized warning that identifies each rejected theme and the validation reason. If `tui.defaultTheme` does not identify a remaining configured or built-in theme, Threadsmith reports that setting separately and uses `system`. Catalog-wide safety-limit failures also report the reason and fail closed to `system` rather than terminating the session.

For example:

```json
{
  "tui": {
    "defaultTheme": "project-blue",
    "themes": [
      {
        "id": "project-blue",
        "name": "Project Blue",
        "styles": {
          "Default": { "foreground": null, "background": null },
          "ComposerPrompt": { "foreground": "brightcyan", "bold": true },
          "ThinkingIndicator": { "foreground": "yellow", "dim": true },
          "ToolSuccess": { "foreground": "green" },
          "ToolFailure": { "foreground": "brightred", "bold": true },
          "SelectionHighlight": {
            "foreground": "black",
            "background": "#5FAFFF",
            "bold": true
          }
        }
      }
    ]
  }
}
```

`NO_COLOR`, redirected output, and limited terminals use plain-text fallback without changing semantic words or markers.

The composer-adjacent status row can show working folder, repository, current local Git branch, model profile name (without the redundant provider model id), reasoning, governed context use, and cumulative provider token usage. It remains ordinary native scrollback rather than a cursor-managed pinned footer. Disable it with `tui:footer:enabled=false`.

### Interactive Markdown answers

Ordinary interactive model answers render as complete semantic Markdown documents by default. Threadsmith buffers each contiguous answer block while `THINKING` remains active, then writes it once before a tool, status, diagnostic, completion, or other visible lifecycle boundary. Every new interactive model answer begins after one blank line, including answers that follow tool activity; redirected raw output does not gain this presentation separator. Native scrollback is never rewritten and a raw duplicate is not printed. Headings remove their source `#`/setext delimiters and use semantic styling plus block spacing; H1 and H2 additionally use bounded double/single underline rules so they remain visibly distinct even when terminal decoration is unavailable; emphasis, strikethrough, lists and tasks, blockquotes, inline and fenced code, public HTTPS links, thematic breaks, and pipe tables use host-owned semantic roles and structural layout. Narrow tables degrade to labeled rows. `NO_COLOR` removes decoration without restoring heading delimiters or changing words, structural markers, indentation, or layout.

Raw Markdown remains authoritative for conversation state, persistence, context, restoration, and headless output. Markdig is used only inside the TUI parser; HTML is inert, unsafe link schemes are plain visible text, parser/size failures fall back once to visibly escaped source, and terminal controls never pass through the interactive source or fallback path unchanged.

Set `tui:renderMarkdown=false` to restore terminal-safe model-source chunk cadence:

```json
{
  "tui": {
    "renderMarkdown": false
  }
}
```

The setting follows normal layered configuration precedence and is snapshotted by the interactive shell. It does not affect reasoning, tool/MCP markers, diffs, status, historical transcript restoration, or headless output.

When a model emits recoverable malformed or invalid tool, plan, mutation, pre-mutation, or post-apply validation output, Threadsmith appends bounded corrective feedback to the active turn and asks the model to retry rather than silently repairing the request. If one sibling in a tool batch is invalid before execution, the whole batch is rejected before any sibling runs; after a successful correction, rejected corrective messages are removed from future model history while successful executed evidence remains. `execution:maxCorrectiveTurns` is the single correction budget; exhaustion fails closed without approving, staging, or executing invalid output.

Operation durations are enabled by default through `tui:showOperationDurations`. One Boolean controls interactive request, ordinary-tool, extension-tool, and MCP duration text together. Active request timing covers the complete accepted turn and resumes from the original start after a tool continuation. Ordinary-tool completion timing covers only `ITool.ExecuteAsync`; MCP completion timing covers the remote transport invocation. Completed rows use compact invariant formatting (`47ms`, `8.6s`, `1:02`, `1:02:03`). Missing or invalid legacy timing is omitted rather than shown as zero.

Tool activity also includes concise context when a built-in explicitly defines a safe display field. For example, file reads show the repository-relative path and requested line range, listings show their root, searches show the query, and `run_process` shows the command. The host sanitizes, collapses to one line, and rune-safely bounds this detail before it reaches activity events or the terminal. Process-command display additionally masks common named CLI credential switches such as `--api-key`, `--password`, and `--client-secret`, including whitespace-separated values; avoid placing credentials directly on command lines because no redactor can recognize every application-specific syntax. Raw argument objects, extension arguments, and MCP arguments remain hidden by default.

```text
 TOOLS: read_file - completed · 47ms
   └ lines 1-200, src/Program.cs

 TOOLS: run_process - completed · 8.6s
   └ dotnet test src/Threadsmith.sln
```

Structured plan proposals, plan auto-approval notices, mutation proposal status, generic correction status, and applied mutation notices use the same one-character-indented interactive lifecycle block family. Proposal bodies, steps, mutation-attempt rows, correction reasons, and applied-mutation detail are guided muted text; auto-approval shows plan-approval provenance and, when prior TUI context explains the classification, a concise risk basis. It does not imply mutation approval.

```text
 PLAN: revision 1
 │ Update the formatter output.
 │
 │ Steps:
 └ 1. Add shared lifecycle blocks - Tool, plan, and semantic-check output align.

 PLAN: auto-approved
 │ Revision: 1
 │ Risk: High
 │ Risk basis: model declared 1 risk; 1 file affected
 │ Policy: AutoApproveAllValid
 └ Reason: Policy AutoApproveAllValid approved a High risk plan after sanity checks.

 MUTATION: Preparing preview
 └ Attempt: 1/4

 CORRECTION: Retrying model request
 │ Attempt: 1/3
 ├ Category: MutationProposal
 └ Reason: ReplaceText expectedText was not found in 'src/File.cs'.

 MUTATION: Applied under the active approval policy
 │ Mutation applied: src/File.cs
 └ The expression uses the approved value.

 MUTATION: Validating applied mutation
 └ Stages: compile, diagnostics, tests
```

Adjacent visible lifecycle blocks are separated by exactly one presentation-owned blank line. Semantic baseline checks may be labeled as pre-apply baseline capture when they occur after preview but before mutation application.

```json
{
  "tui": {
    "showOperationDurations": false
  }
}
```

The setting follows normal machine/user/repository/session/CLI/environment/secrets precedence and affects presentation only. Disabling it preserves `THINKING`, `TOOLS`, `MCP`, and outcome words, performs no periodic elapsed repaint, and does not alter execution, telemetry, persistence, or headless structured output.

## Extensions

Threadsmith extensions use stable host-owned contracts and load into collectible `AssemblyLoadContext` generations. Extensions may contribute governed capabilities and model preferences.

Operational principles:

- extension implementations depend on `Threadsmith.Extensions.Abstractions`, not host runtime internals;
- terminal-library, provider-SDK, and extension implementation types do not enter durable host state;
- capability invocation uses leases and bounded per-extension budgets;
- unload drains active invocations and verifies collectible generations;
- hot replacement publishes the new generation without allowing an old unload to remove it;
- load-context isolation is not a security boundary, so in-process extensions must be trusted.

The interactive `/extensions` surface supports browsing, loading, and unloading configured extensions. Repository-level selection uses `.threadsmith/extensions.json`.

Extension authors should read [extension-authoring/authoring-guide.md](extension-authoring/authoring-guide.md).

## Governed skills and reusable workflows

Milestone 12 provides reusable declarative procedures without making package content executable or authoritative. Threadsmith searches organization, machine, user, repository, and maintained catalogs by bounded metadata. Startup does not open instruction/schema/reference bodies. A candidate reports scope, id, semantic version, SHA-256 digest, publisher/source, declared requirements, verification state, and enablement state.

Skills are data packages, not extensions. They cannot ship assemblies or scripts, add tools, grant trust, approve work, create agents, schedule tasks, mutate a repository, access the network/processes directly, or claim build/test success. Extension packages remain the executable capability mechanism. Skill workflows can only use existing host tools and return closed typed host-action proposals.

### Claude-style compatibility

Threadsmith also discovers bounded metadata from `.claude/skills/<name>/SKILL.md` in the repository and user profile. `/skills list` marks these entries with the `claude:` source format, and `/skills inspect claude:<scope>:<name>` shows the pinned contract, mapped/unavailable tools, restrictions, and unsigned-source warning.

Discovery reads only safe bounded frontmatter from explicit nonlinked roots. Explicit activation resolves the current catalog generation, reparses and compares frontmatter, revalidates confinement, rejects linked/reparse roots and descendants plus unsafe YAML features, loads strict-UTF-8 instructions/text resources under aggregate limits, and computes a deterministic SHA-256 identity over path, length, and raw bytes. Scripts and binaries are identity inputs but never execute automatically. `allowed-tools` is advisory; mappings still pass through repository availability, trust, phase, consent, and the central tool policy. Hook, agent, fork, dynamic-shell, or unmapped requirements remain restricted or unsupported rather than acquiring authority.

Use `/skills verify claude:<scope>:<name>` to compute and inspect the exact identity, `/skills enable claude:<scope>:<name>` to persist an external digest/source authorization outside the repository, and `/skills use claude:<scope>:<name> <json>` to invoke it through the same Plan-39 workflow/checkpoint boundary used by native packages. Headless skill commands and `invoke_skill` accept the same selector. Source changes invalidate the old authorization and any resume attempt under its digest.

See [the pinned compatibility contract](skill-compatibility-spec-v1.md) and [skill operations](operations/skills.md). Native Plan-39 signed packages retain stronger verification and distinct `native:` listing labels.

### Finding and selecting skills

```text
/skills list [text]
/skills refresh
/skills inspect <selector>
/skills provenance <selector>
/skills install <archive-path> <source>
/skills uninstall <selector>
```

Selectors become more specific from left to right:

```text
fix-analyzer-warnings
fix-analyzer-warnings@1.0.0
Maintained:fix-analyzer-warnings@1.0.0
Maintained:fix-analyzer-warnings@1.0.0+<sha256>
```

If more than one candidate matches, Threadsmith fails with an ambiguity message rather than applying scope or directory precedence. Invocations and pins retain the exact scope/id/version/digest.

### Verification, enablement, and revocation

```text
/skills verify <selector>
/skills enable <selector>
/skills disable <selector>
/skills pin <selector>
```

`install` accepts a local ZIP path plus its configured provenance source, verifies it in quarantine, and atomically imports it into the user content-addressed catalog. Install a new immutable version and pin it to update; pin an older installed version to roll back. `uninstall` is user-scope only and refuses pinned packages or packages retained by active workflows.

Verification re-reads the manifest, validates every declared hash/length/path, rejects undeclared files and links, and then applies revocation plus signature/exact-allowlist policy. A valid signature proves origin and integrity; it does not independently grant tool/trust/action authority and does not automatically enable third-party content. `enable` records an exact digest/publisher/source authorization outside the repository in `%USERPROFILE%\.threadsmith\skill-policy.json`. Repository files cannot add trusted signers, allowlists, enablement, or revoke exceptions. Revocation wins at the next verification/action/resume boundary.

Maintained packages are enabled after their shipped integrity verifies:

- `fix-analyzer-warnings` — investigates supplied analyzer diagnostics and proposes a governed remediation plan;
- `upgrade-package` — assesses one Central Package Management upgrade and proposes compatibility/rollback/validation steps;
- `review-pr` — returns bounded security, test, performance, and architecture findings without publishing or mutating.

### Copyable maintained-skill examples

Start at `TrustedRead`, inspect the exact package, and use one-line JSON because the remainder of `/skills use` is parsed as the input document:

```text
/trust read
/skills inspect Maintained:fix-analyzer-warnings@1.0.0
/skills verify Maintained:fix-analyzer-warnings@1.0.0
/skills use Maintained:fix-analyzer-warnings@1.0.0 {"diagnostics":["CA1822: Member 'Normalize' does not access instance data","IDE0058: Expression value is never used"],"scope":["src/Example/Normalizer.cs","tests/Example.Tests/NormalizerTests.cs"]}
```

A successful analyzer procedure returns schema-versioned `propose_plan` arguments and pauses with a typed `ProposePlan` action. Review and submit that action through the ordinary planning boundary. Do not manufacture a successful continuation. Only after the host has accepted the plan should the adapter continue the workflow with the actual host result, for example:

```text
/skills continue <invocation-id> {"accepted":true,"planId":"<host-plan-id>"}
```

Plan acceptance still does not apply edits. Plan 37 approval, implementation, exact-diff policy, transaction, build/test validation, and correction follow normally.

The conversational equivalent is to ask the model to use an exact selector and provide the typed input, for example: `Use Maintained:fix-analyzer-warnings@1.0.0 with diagnostics [...] and scope [...]`. During eligible evidence collection at `TrustedRead` or higher, the model may call `invoke_skill`; the host performs the same selection, schema, compatibility, budget, and workflow checks as `/skills use`. The tool is not available as a way to invoke nested skills or during an ineligible phase.

Assess a Central Package Management upgrade without implicitly restoring packages or accessing the network:

```text
/skills inspect Maintained:upgrade-package@1.0.0
/skills use Maintained:upgrade-package@1.0.0 {"packageId":"Microsoft.Extensions.Logging","targetVersion":"10.0.1","constraints":["Keep versions in Directory.Packages.props","Do not change target frameworks","Include rollback and focused tests"]}
```

Run a bounded review over explicit paths and focuses:

```text
/skills inspect Maintained:review-pr@1.0.0
/skills use Maintained:review-pr@1.0.0 {"changeSummary":"Add repository-scoped API-key rotation and audit events","paths":["src/Example/Auth","tests/Example.Auth.Tests"],"focus":["security","tests","performance","architecture"]}
```

`review-pr` produces evidence-backed structured findings; it does not publish, approve, merge, create reviewers, or mutate. Use ordinary conversation or a custom delegation-capable workflow when independent Plan-38 reviewers are required.

### Model selection for skills

Skills do not name arbitrary provider endpoints and cannot download or activate models. Their manifest narrows the already configured model catalog. For example:

```json
{
  "requirements": {
    "model": {
      "workloads": [ "Planning", "CodeEdit" ],
      "requiresToolCalls": true,
      "requiresStructuredOutput": true,
      "minimumContextWindow": 16384,
      "allowedProfiles": [
        { "value": "4d36e96e-292b-4c25-bb63-2f63821d5729" }
      ],
      "deniedProfiles": []
    }
  }
}
```

`allowedProfiles` is a strict allowlist of configured model-profile GUIDs; the first compatible entry is also the package preference. `deniedProfiles` removes profiles even when their capabilities otherwise match. Leaving `allowedProfiles` empty lets host selection choose among compatible configured profiles. The host then applies workload compatibility, tool-call/structured-output capabilities, minimum context, provider sensitivity policy, configured default/preference, and cost policy. The selected profile ID is frozen in the workflow checkpoint, so resume does not silently switch models.

Use `/skills inspect <selector>` before invocation to see declared workloads and compatibility denials such as `no-compatible-model`. Configure provider/model capabilities and intended workloads in user/repository `.threadsmith/providers.json`; see [Model providers, secrets, and reasoning](#model-providers-secrets-and-reasoning). `/reasoning` controls supported ordinary session-turn reasoning. Skill procedure requests do not expose a manifest reasoning selector and currently use the host's fixed request policy; neither skill text nor a package manifest can elevate reasoning, switch the selected profile after admission, expose a sensitive request to a prohibited provider, or borrow another workflow's budget.

Examples:

1. For local-only sensitive review, permit sensitive data on the intended local profile and place only that profile ID in `allowedProfiles`.
2. For inexpensive analyzer planning, leave `allowedProfiles` empty and configure a compatible default or lower-cost `Planning`/`CodeEdit` model.
3. For a large review, raise the model's configured context window only when the provider really supports it; a skill cannot override the provider profile or its host-level context ceiling.

### Skills that propose subagents

A custom skill may declare bounded Plan-38 role templates and emit only `ProposeDelegation` or `RequestReviews`. It cannot create tasks, choose concurrency dynamically, recurse into another delegation layer, or start children itself. This abbreviated authoring fragment declares two eligible reviewers:

```json
{
  "budget": {
    "delegatedChildren": 2,
    "parallelChildren": 2,
    "worktrees": 0,
    "reviewerFindings": 32
  },
  "agents": [
    {
      "role": "SecurityReviewer",
      "maximumChildren": 1,
      "outputSchemaPath": "schemas/reviewer-output.json",
      "budget": { "modelTokens": 12000, "toolCalls": 12, "evidenceItems": 32, "files": 64, "bytes": 4194304, "mutations": 0, "processes": 0, "builds": 0, "tests": 0, "corrections": 0, "wallTime": "00:05:00" }
    },
    {
      "role": "TestReviewer",
      "maximumChildren": 1,
      "outputSchemaPath": "schemas/reviewer-output.json",
      "budget": { "modelTokens": 12000, "toolCalls": 12, "evidenceItems": 32, "files": 64, "bytes": 4194304, "mutations": 0, "processes": 0, "builds": 0, "tests": 0, "corrections": 0, "wallTime": "00:05:00" }
    }
  ],
  "workflow": {
    "schemaVersion": 1,
    "workflowId": "review-with-specialists",
    "steps": [
      { "stepId": "scope", "kind": "invokeProcedure", "dependsOn": [], "instructionAsset": "instructions/scope.md", "inputSchemaAsset": "schemas/input.json", "outputSchemaAsset": "schemas/delegation-request.json", "maximumIterations": 1 },
      { "stepId": "request-reviews", "kind": "requestReviews", "dependsOn": [ "scope" ], "outputSchemaAsset": "schemas/delegation-result.json", "maximumIterations": 1 }
    ]
  }
}
```

The complete manifest must still declare and hash every referenced asset and satisfy aggregate package/host budgets; see [Declarative skill authoring](skill-authoring.md). At runtime:

1. `/skills use` runs the bounded `scope` procedure with the selected skill model.
2. The workflow pauses and displays the complete typed `ProposeDelegation` payload.
3. The host validates that request against Plan 38: current trust, sensitivity, approved plan where mutation is involved, eligible roles, one-level depth, paths, tools, models, deadlines, child/aggregate budgets, and non-overlap all still apply.
4. Only an accepted host request creates a delegation ID. Inspect it with `/agents <delegation-id>` and cancel with `/agents <delegation-id> cancel` or `cancel-child <assignment-id>`.
5. After the delegation reaches its authoritative structured join, the adapter supplies that real result through `/skills continue <invocation-id> <delegation-result-json>`. The next workflow step receives only the schema-valid structured result—not raw child transcripts or hidden reasoning.

Model selection is hierarchical. The skill model selected above prepares the proposal; each accepted child receives a host-selected model/reasoning choice constrained by the parent, repository policy, role template, sensitivity, and remaining aggregate budget. A package preference can narrow candidates but cannot force an incompatible model, elevate a child, or bypass the parent ledger. Implementation agents additionally require an approved plan, host-proven non-overlapping ownership, isolated worktrees, parent restaging, a fresh aggregate diff decision, and aggregate validation.

### Package lifecycle example

Use immutable selectors during update and rollback:

```text
/skills install C:\packages\contoso-review-1.0.0.zip contoso-internal
/skills verify User:contoso-review@1.0.0
/skills enable User:contoso-review@1.0.0+<digest-1>
/skills pin User:contoso-review@1.0.0+<digest-1>
/skills install C:\packages\contoso-review-1.1.0.zip contoso-internal
/skills pin User:contoso-review@1.1.0+<digest-2>
/skills pin User:contoso-review@1.0.0+<digest-1>
/skills uninstall User:contoso-review@1.1.0+<digest-2>
```

The second-to-last command rolls back future selection without modifying an already-running workflow. Uninstall refuses the currently pinned package and any package retained by a nonterminal checkpoint. Trusted signer keys, source/digest allowlists, organization denies, and revocations must already exist in repository-excluding configuration; typing a source name does not establish trust.

### Invocation and workflow control

```text
/skills use <selector> <json-input>
/skills status <invocation-id>
/skills continue <invocation-id> <host-result-json>
/skills resume <invocation-id>
/skills cancel <invocation-id>
```

Invocation validates bounded JSON input and current host/tool/trust/model/phase requirements before loading content. It loads only current-step assets, rechecks hashes, uses strict UTF-8 and sanitization, omits optional references under pressure, and fails if required content does not fit. Procedure turns advertise only declared available tools and still use the central tool pipeline.

A workflow may pause with a typed host action such as `ProposePlan`, `ExecuteApprovedPlan`, `ProposeDelegation`, `Validate`, or `AskUserInput`. The proposal does not perform the action. Normal planning, approval, exact-diff, transaction, build/test validation, Plan-38 scheduling/worktrees/reviews, cancellation, and authoritative outcomes remain mandatory. `continue` accepts the result only after the corresponding host action completes and validates it against the declared step-result schema.

Workflow checkpoints pin package identity, input, selected model/tools, budget, completed steps, attempt/generation, and next action. Resume revalidates the exact package and current policy; it never switches to a newer version or replays a completed effect. See [governed skills operations](operations/skills.md) for catalog locations, trusted configuration, import/quarantine behavior, detailed commands, and recovery.

## Lifecycle hooks and policy automation

Milestone 13 adds opt-in typed automation at repository, model, tool, planning, mutation, validation, correction, run, extension, and MCP lifecycle boundaries. With no handlers configured, behavior is unchanged.

Repository configuration may declare handlers under `hooks:repositoryHandlers`, but declarations start disabled and cannot approve themselves. Interactive users use `/hooks list`, `/hooks inspect <id>`, `/hooks enable|disable <id>`, `/hooks test <id>`, `/hooks approve|revoke <id>`, and `/hooks audit [id]`. The matching `HeadlessShell` methods expose the same operations for automation. Both surfaces dispatch the same Core commands (`ListHooksCommand`, `InspectHookCommand`, `ApproveRepositoryHookCommand`, `RevokeRepositoryHookCommand`, `SetHookEnabledCommand`, `TestHookCommand`, and `QueryHookAuditCommand`); a test command invokes only its selected handler.

Repository handlers are always advisory and fail-open. Only repository-excluding managed organization/machine/user configuration can grant blocking or fail-closed authority, and only at eligible pre-action points for an immutable handler identity and allowlisted denial codes. Completed/terminal hooks cannot undo or invalidate completed work. No result is an approval or a command.

Every invocation is bounded by timeout, input/output size, concurrency, retry, call-chain depth, effective data scope, and logical-secret scope. The host abandons and discards late results when a handler ignores cancellation. `MutationStaged` runs once after exact-diff staging, `MutationApplied` runs once after the completed transaction rather than once per file, repository plan hooks receive the open repository identity, and `McpConnected` runs after successful auto-connect publishes imported capabilities. HTTP requires HTTPS except literal loopback development endpoints and does not follow redirects. Executable arguments are never shell interpolated. MCP must already be connected, and extension invocation retains existing lease/budget ownership.

See [Lifecycle hook operations](operations/lifecycle-hooks.md), [hook authoring](hook-authoring.md), and [ADR-35](architecture/adr-35-host-owned-lifecycle-hooks.md).

## Headless and automated use

The headless adapter uses the same command dispatcher and policy path as the TUI. Active-model automation uses `ListActiveModelsCommand`, `GetActiveModelSelectionCommand`, `SelectActiveModelCommand`, and `SetActiveReasoningCommand`; selection and persistence behavior is identical to `/models` and `/reasoning`. Skill automation uses `RefreshSkillsCommand`, `ListSkillsCommand`, `GetSkillCommand`, `GetSkillCompatibilityCommand`, `InstallSkillCommand`, `UninstallSkillCommand`, `VerifySkillCommand`, `SetSkillEnabledCommand`, `PinSkillCommand`, `InvokeSkillCommand`, `ContinueSkillCommand`, `ResumeSkillCommand`, `GetSkillInvocationCommand`, and `CancelSkillInvocationCommand`; verification, schema, compatibility, workflow, persistence, and restoration behavior is identical to `/skills`.

Headless mode writes model/tool activity to standard output and uses these primary exit codes:

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Failure. |
| `2` | Discovery needs trust/selection, or an exact MCP profile/capability was not found. |
| `3` | A model-proposed web destination needs exact authorization, or MCP policy/source eligibility denied the operation. |
| `4` | MCP authentication, unsupported-authentication, or revocation state requires user action. |
| `124` | An MCP lifecycle operation timed out. |
| `130` | Cancellation. |

Inspect-only discovery:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo
```

Trusted discovery with explicit selection:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo --trust TrustedRead --solution src\MyRepo.sln
```

Trusted headless startup auto-loads a valid remembered solution. If multiple candidates remain, it lists them and exits `2` rather than prompting or choosing arbitrarily.

For exit `3`, headless output includes the sanitized `DirectAuthorizationRequired` tool error. Create an exact grant with `HeadlessShell.AuthorizeWebFetch` or `AuthorizeWebFetchChain`, then retry under a fresh invocation; headless mode never opens a prompt or reads opportunistically from stdin.

Redirected output avoids interactive styling and session-status control sequences. Cancellation should be propagated by the invoking host; Threadsmith normalizes user cancellation to exit code `130`.

## Persistence, retention, and diagnostics

Threadsmith creates a repository-local SQLite database at `.threadsmith/threadsmith.db` and stores large sanitized bodies by content hash under `.threadsmith/artifacts`. Migration 5 adds bounded skill verification provenance, immutable package pins, and workflow checkpoints; age-based retention removes expired workflow checkpoints while retaining small trust/pin provenance until explicitly replaced. On every startup the host applies ordered transactional migrations, runs the configured redaction audit, and then performs one retention pass. A failed schema migration rolls back; unsupported restored event versions are represented as partial legacy state instead of crashing the whole restore.

The repository configuration may override the locations and cleanup policy:

```jsonc
{
  "persistence": {
    "path": ".threadsmith/threadsmith.db",
    "artifactDirectory": ".threadsmith/artifacts",
    "retention": {
      "enabled": true,
      "sessionAgeDays": 30,
      "metadataOnly": false,
      "retainFullPrompts": true,
      "retainFullModelOutput": true,
      "retainProcessLogs": false,
      "retainSourceExcerpts": true,
      "retainDiffs": true,
      "retainTelemetry": false,
      "retainSessionSummaries": true,
      "retainConversationBodies": false,
      "conversationMessageBodyAge": "30.00:00:00"
    },
    "redactionAudit": {
      "enabled": true,
      "repairArtifacts": true
    }
  }
}
```

`sessionAgeDays` and `conversationMessageBodyAge` must be positive. `metadataOnly` removes eligible aged artifact bodies regardless of their kind. Otherwise each `retain...` switch decides whether an eligible aged kind is deleted. `retainConversationBodies: false` detaches old visible message bodies after their independent age window while keeping governed memory and provenance. Retention runs at startup rather than continuously, so a long-running process does not clean newly expired records until its next launch.

The redaction audit is defense in depth, not a substitute for keeping secrets out of prompts and tool output. Event history is append-only and findings there are reported but not rewritten. With `repairArtifacts: true`, unsafe artifact bodies are sanitized. Disabling the audit or retention increases local-data exposure and should be a deliberate repository-owner decision.

Milestone 8 also supplies the bounded `DiagnosticBundleGenerator` contract used by tests and future support surfaces. Bundle entries are sanitized and size-limited, and a canary-secret gate verifies generated ZIP content. There is not yet an interactive or headless command that generates a bundle; consequently the `diagnostics` example keys are reserved contract settings rather than an available user command.

## MCP connection profiles

Threadsmith uses one host-owned MCP manager for startup auto-connect, repository transitions, interactive commands, headless commands, and shutdown. Profiles marked `autoConnect` in repository-excluding trusted machine/user/environment configuration connect best-effort during startup; repository-owned profiles cannot authorize command execution or grant themselves trust. An eligible profile's connection failure is sanitized and does not prevent the shell from opening; malformed profile configuration fails closed during composition. Defined profiles remain visible while disconnected.

Interactive lifecycle commands are:

```text
/mcp list
/mcp inspect [profile]
/mcp connect|disconnect|reconnect [profile]
/mcp capabilities [profile] [kind]
/mcp capability [profile] [capability]
/mcp enable|disable [profile] [tool]
/mcp resource read [profile] [resource-or-template] [key=value ...]
/mcp prompt get [profile] [prompt] [key=value ...]
/mcp auth|logout|revoke|switch-account|diagnose [profile]
```

Omitted profile or capability IDs open bounded numbered selectors. `logout`, `revoke`, and `switch-account` show the exact profile and sanitized endpoint before confirmation. Switch-account asks whether to perform local logout or advertised remote revocation first. Resource and prompt values containing spaces may be quoted (for example, `name="review this file"`). Resource and prompt output is marked untrusted, bounded, and displayed only for the explicit operation; aggregate truncation remains visible when server items are omitted, and content is not silently added to model context. `/mcp enable|disable` delegates to the ordinary tool-availability authority. Imported tools start disabled and require an exact repository-bound, capability/schema-digest-bound approval stored outside repository control in the user-owned `~/.threadsmith/mcp-tool-approvals.json` file. Repository `tools:enabled` and `tools:defaultEnabledOverrides` entries cannot grant that approval, so a different repository or changed server schema fails closed until explicitly reviewed and enabled again. Imported tool ids are profile-qualified canonical ids such as `profile:tool`; providers that require narrower function-name syntax receive an internal per-request alias, but host configuration, enablement, policy, and diagnostics continue to use the canonical id. Servers that advertise list-change notifications trigger a debounced complete rediscovery within the 256-capability connection bound; registry publication is replaced atomically, the manager generation advances, and previously resolved tools from the replaced capability generation can no longer invoke.

Headless automation uses the same manager and stable JSON result envelope:

```powershell
threadsmith --mcp list
threadsmith --mcp connect local-docs
threadsmith --mcp capabilities local-docs tools
threadsmith --mcp resource-read local-docs <capability-id> name=value
threadsmith --mcp logout remote-sso --confirm
threadsmith --mcp switch-account remote-sso --confirm --revoke-current
threadsmith --mcp revoke remote-sso --confirm --allow-local-cleanup
```

Exact IDs are required headlessly. When `--mcp` selects management mode, `--confirm` is mandatory for identity mutations, `--revoke-current` selects revocation before switch-account, and `--allow-local-cleanup` permits local cleanup only after an unconfirmed remote revocation. Without `--mcp`, these tokens remain unchanged ordinary conversational request text. MCP results use exit `0` for success, `2` for not found, `3` for ineligible/policy denial, `4` for authentication/revocation outcomes requiring action, `124` for timeout, `130` for cancellation, and `1` for other failure. Headless mode never substitutes an interactive selector or confirmation prompt.

The accepted profile shape is:

```jsonc
{
  "mcp": {
    "defaultDrainKillTimeoutSeconds": 10,
    "profiles": [
      {
        "id": "example",
        "name": "Example server",
        "transport": "stdio",
        "command": "npx",
        "arguments": ["-y", "package-name"],
        "trust": "TrustedRead",
        "secretScope": [],
        "startupTimeoutSeconds": 30,
        "requestTimeoutSeconds": 60,
        "drainKillTimeoutSeconds": 10,
        "allowedCapabilities": ["tools"],
        "environment": {},
        "workingDirectory": ".",
        "headers": {},
        "oauth": { "enabled": false },
        "autoConnect": true
      }
    ]
  }
}
```

Valid transports are `stdio`, `sse`, and `http`; valid trust values are `Untrusted`, `TrustedRead`, `TrustedExecution`, and `FullyTrusted`. An untrusted profile cannot connect. Capability values accept singular or plural `tool(s)`, `resource(s)`, `resource-template(s)`, and `prompt(s)`; unknown values fail startup rather than broadening access. Omitting the capability list permits all four kinds at the adapter boundary.

A stdio command must be a bare executable name—path-qualified commands are rejected. Arbitrary parent environment variables are not inherited; Threadsmith forwards a curated OS startup set plus explicit profile values and scoped secrets. SSE/HTTP commands are absolute endpoints and imported tools expose the endpoint host to the standard network allowlist.

HTTP `headers` support ordinary values and static-token SSO. A `secrets:` header value resolves only when the exact reference is also in `secretScope`; values are not retained in connection status or logs.

For interactive SSO, set `oauth.enabled` to `true`, requested `scopes`, and a loopback `redirectPort` (or `0` for an ephemeral port). You may provide a pre-registered `clientId`; if it is omitted, an explicit connect or authentication operation asks the MCP SDK to use advertised OAuth metadata plus dynamic client registration for a public native PKCE client. A configured `clientSecret` requires `clientId` and must be a logical `secrets:` reference included in `secretScope`. OAuth is HTTP/SSE-only and cannot be combined with an `Authorization` header. Threadsmith uses the official MCP SDK for protected-resource and advertised authorization-server discovery, dynamic registration when needed, authorization-code + PKCE, state and issuer validation, token exchange, bearer attachment, and refresh. The configured scopes are an upper bound even when the server advertises broader scopes. `oauth.discoveryUrl` is rejected because the pinned SDK does not support an arbitrary discovery-document override.

Interactive mode binds the localhost callback listener before opening the system browser. Automatic startup and repository-rebind connections may reuse or refresh a coherent cached OAuth identity but cannot invoke dynamic registration or the authorization callback; missing or unusable identity remains an explicit connect/authentication action instead of creating a remote client, opening a browser, or waiting for input. Headless mode writes the authorization URL and complete pasted-callback prompt to standard error only for an explicit connect/authentication operation, bypasses optional extension startup even when `--tui` is also present, and preserves the single-JSON standard-output contract. Access tokens, refresh tokens, dynamically registered client ids/secrets, and token metadata are cached outside the repository in the user-owned `~/.threadsmith/mcp-oauth-tokens.json` secret cache as one atomically replaced grant under `mcp:oauth:<profileId>:*`; each read uses one immutable grant snapshot, superseded grants and staged registrations are pruned, Unix cache files are owner-only, malformed optional cache content is recoverable, and credentials never appear in connection status, logs, projections, diagnostic bundles, or repository configuration. Cached dynamic-registration client credentials are reused only when their redirect URI exactly matches the current callback URI; with `redirectPort: 0`, a later explicit re-authentication may require local logout to force a fresh registration. Listing, inspection, and diagnostics never launch a browser. Local logout clears only the selected profile namespace and makes no remote claim. Remote revoke requires a same-origin advertised HTTPS revocation endpoint and uses the grant-bound `none`, `client_secret_post`, or `client_secret_basic` authentication method; configured client credentials remain authoritative and their secret reference is resolved afresh so rotation takes effect. Metadata redirects are not followed, request timeouts and other unconfirmed outcomes remain explicit, and explicitly authorized local-only cleanup still clears the selected cache after a timeout. One identity remains cached per profile; switch-account replaces it rather than retaining multiple accounts. Stdio OAuth remains out of scope. See [MCP connections](operations/mcp-connections.md) for command details, lifecycle behavior, and live-test variables.

## Safety model

Threadsmith provides governed execution, not a general operating-system sandbox.

Hard boundaries include:

- normalized repository containment for configured, selected, and mutated paths;
- rejection or skipping of symbolic links, junctions, prohibited paths, and reserved/inaccessible entries as appropriate;
- immutable baseline hashes and turn-boundary invalidation;
- explicit trust and approval checks;
- central tool policy and bounded result handling;
- no destructive Git operations;
- no writes outside approved repository roots;
- logical secret references resolved only at the final invocation boundary;
- transactional mutation with rollback;
- tracked process-tree termination on cancellation and timeout;
- sanitization and bounding of terminal, model, extension, diagnostic, and repository-provided content.

`AssemblyLoadContext`, scripting restrictions, and in-process policy checks reduce risk but do not isolate hostile native or managed code from the operating system. Use trusted repositories, models, extensions, build logic, and automation settings.

## Troubleshooting

### Startup reports `TextOnly` semantic confidence

`TrustedRead` deliberately avoids MSBuild evaluation. Reopen or upgrade to `TrustedBuild` when compiler-backed symbol discovery is required.

### Multiple solutions are reported in headless mode

Pass `--solution <repository-relative-path>`. A successful selection is remembered for future launches.

### A remembered solution was not loaded

Confirm the file still exists beneath the repository and that `.threadsmith/config.json` contains nested `solution.path`. Missing entries are cleared automatically; escaping, prohibited, or linked paths are rejected.

### A tool is missing

Check, in order:

1. `/tools` availability;
2. `tools:enabled` and `tools:disabled`;
3. `tools:allow` and `tools:deny` invocation policy;
4. required trust level;
5. semantic confidence for compiler-backed tools;
6. extension load state for extension tools.

Essential tools cannot be disabled.

### A tool invocation is denied

Inspect the displayed reason for trust, policy, approval, path, executable, network, timeout, cancellation, or output-bound failure. Enabling a tool does not bypass invocation policy.

### An MCP profile is missing or will not connect

Run `/mcp list` and `/mcp inspect`. Profiles come from repository-excluding trusted configuration; an `Untrusted` profile is visible but ineligible. Check the bare stdio executable or HTTPS endpoint, trust, allowed capability kinds, secret references, and startup timeout. Use `/mcp diagnose` for structured safe checks. A profile-local startup failure is nonfatal to the shell.

### An MCP tool remains disabled after reconnect

This is expected when it has never been enabled or when the server capability/schema digest changed. Inspect the capability, then explicitly run `/mcp enable` after review. Enabling records the current repository's exact reviewed MCP schema in the user-owned approval store; repository configuration alone cannot enable it. Enabling does not bypass repository trust, tool invocation, executable, network, secret, hook, approval, timeout, or output policy.

### MCP logout or revocation did not remove remote access

`logout` is local-only by design. Use `revoke` when the authorization server advertises RFC 7009 support. An unsupported or unconfirmed result is not remote success; retry later or explicitly choose local-only cleanup. Static-token profiles require rotation/removal in the external secret source.

### Model requests fail immediately

Verify the endpoint is the complete chat-completions URL, the profile advertises the capabilities required by the interactive flow, and the logical secret reference resolves. Credentials are intentionally absent from logs.

### Terminal output has no colors or status row

Styling is suppressed under `NO_COLOR`, redirected output, or limited-terminal detection. The status row is also absent when `tui:footer:enabled` is false.

### Builds or tests execute repository code

This is expected at `TrustedBuild` and above. Downgrade future use by reviewing persisted trust outside the active session and do not grant build trust to an untrusted repository.

### Script execution times out

The worker process tree is terminated. Reduce the work, increase `tools:config:csharp_script:timeout_ms` within accepted bounds, or keep scripting disabled. Restrictions do not make arbitrary code safe.

## Further reference

- [Opening a repository](operations/opening-a-repository.md)
- [Session lifecycle, resume, and clone](operations/session-lifecycle.md)
- [Interactive commands and keys](operations/keyboard-shortcuts.md)
- [Tool runtime operations](operations/tools.md)
- [MCP connections and lifecycle](operations/mcp-connections.md)
- [Model providers](operations/model-providers.md)
- [TUI themes](operations/tui-themes.md)
- [Project prompt append](operations/project-prompt-append.md)
- [Cache-optimized context](operations/cache-optimized-context.md)
- [Extension authoring](extension-authoring/authoring-guide.md)
- [Architecture decisions](architecture/)
- [Implementation roadmap](implementation-plans/README.md)
- [Maintained manual test plan](implementation-plans/manual-test-plan.md)

## Maintaining this guide

This is the primary user-facing reference. Update it in the same change whenever implemented behavior affects installation, startup, commands, configuration, trust, tools, models, extensions, safety boundaries, output, exit codes, or troubleshooting. Keep the README concise and link here for operational detail. Do not document planned behavior as available.
