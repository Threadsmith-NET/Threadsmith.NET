# Session-scoped temporary scripts

**Status:** Complete  
**Delivery track:** Maintenance  
**Prerequisites:** Existing fixed repository scratchpad, built-in file tools, tracked process execution, and C# scripting worker. See [fixed repository scratchpad](maintenance-fixed-repository-scratchpad.md).

## 1 Objective

Keep agent-created temporary C# scripts, analysis output, and routine execution byproducts in `.threadsmith/.scratchpad` for the current session. Prohibit Python scripting for now.

## 2 Architectural Context

Extend the existing scratchpad capability, `write_file`, `run_process`, `csharp_script`, and `IProcessManager`. Preserve normal tool policy, activity, results, cancellation, and parent/child authority. Follow [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions) and read the C# guardrails before implementation.

## 3 Scope

Scratchpad-only script writes, C# reads from the scratchpad and configured accessible paths, process working-directory selection, inspectable C# snippets, temporary/cache environment settings, lifecycle cleanup, and model guidance.

## 4 Non-Scope

Additional governance of arbitrary `run_process` commands, shell parsing, container sandboxing, new execution tools, runtime/package installation, or changes to normal project source editing. Containers are the long-term execution sandbox; they are not part of this change.

## 5 Current State

- The scratchpad is opt-in, requires repository `.gitignore` coverage, and is cleared at session boundaries and application startup/shutdown.
- `write_file` rejects script extensions in context-free input validation.
- Scratchpad policy permits only built-in `search`, `read_file`, and `write_file`.
- `run_process` always starts in the repository root; `csharp_script` sends inline code to a tracked worker starting in the worker installation directory.
- The C# worker blocks the entire `System.IO` namespace independently of `allowed_assemblies`, so changing its working directory alone does not enable file analysis.
- `System-Scratchpad.md` already mentions executable scripts, ahead of tool support.

## 6 Proposed Design

- Permit `.cs` and `.csx` through `write_file` only after invocation-aware validation resolves the destination inside the active scratchpad. Retain current extension rules elsewhere and all existing write checks. Keep Python script extensions rejected; the C# scratchpad API must also reject `.py` and `.pyw` write destinations.
- Briefly update the shared parent/child guidance (`System-RepositoryInspection.md`, included even when scripting is disabled) and the relevant scratchpad/process tool prompts: "If scripting is required, use only the C# scripting host (`csharp_script`). If that tool is not enabled, ask the user to enable it." Prohibit alternate interpreters, Python scripts, and inline/stdin or renamed-script workarounds through `run_process`. Keep this instruction available without the scripting tool description and do not repeat API documentation. This is model guidance plus scoped writer validation, not a claim that arbitrary shell commands are technically confined.
- Add optional `workingDirectory` to `run_process`, defaulting to the repository root. Resolve it through existing path rules; explicitly admit the active scratchpad for this built-in execution path without weakening execution trust, approval, or tool availability checks.
- For scratchpad executions, set `TMPDIR`, `TMP`, and `TEMP` to a scratchpad child directory through `ProcessExecutionRequest.EnvironmentVariables`. Resolve absolute paths in host code; do not change the host environment.
- Require an active scratchpad for `csharp_script`. Save each submitted snippet as a uniquely named `.csx` there, using the existing bounded, validated writer path. Execute the same code through the existing worker with the scratchpad working directory and temporary environment. Return the snippet path with the ordinary result, including worker failures. Do not add a second C# execution mechanism.
- Expose a small host-owned `Scratchpad` API as Roslyn script globals in the existing worker: lazy line reads, bounded byte-chunk reads, and text/binary writes. Support creating files and subdirectories, explicit overwrite, append, and incremental output for large results without buffering the entire output. Scripts may write intermediate data and results as needed within the scratchpad; these operations need no separate approval beyond authorized script execution. Pass the validated scratchpad root and effective read policy through the host-owned worker request, never as model-selected authority. Relative API paths resolve beneath the scratchpad; absolute read paths may also target explicitly authorized read locations. Writes remain confined to the scratchpad. Validate containment, prohibited/protected paths, and links on every open/write. Reuse existing containment/write checks where practical; expose strings/byte arrays rather than raw filesystem handles or unrestricted `System.IO` types.
- Reuse the ordinary file-read policy for repository inputs, including parent/child scope and prohibited paths. Add configured external read folders through the existing permission catalog as `tools:readFile:allowedFolders` (default empty), shared by `read_file` and the C# API through the same path validator. Accept grants only from supported trusted permission sources; apply enforced restrictions and child narrowing. Existing `tools:writeFile:allowedFolders` is write-only and must not imply read access. Pass only resolved host-owned policy data to the worker and invalidate it through the existing rebind/cancellation lifecycle.
- Keep direct `System.IO`, reflection, process, and network access blocked. Make only the supplied scratchpad API available to both compilation validation and execution. Stream large inputs without loading whole files or copying their contents into model context; retain timeout, cancellation, output, and write-size limits. Read authorized large inputs in place; do not require copying them into the scratchpad or model context.
- Reuse session cancellation and process termination/draining before scratchpad cleanup on `/new`, repository transition, and shutdown. Change ordering only where needed; do not clear files while tracked script work remains active.
- Tell parents and eligible children to place temporary C# scripts and generated files in the advertised scratchpad and execute analysis through `csharp_script`. If the tool is disabled, ask the user to enable it; if another prerequisite is unavailable, report that requirement rather than choosing another temporary-script location or interpreter. Existing non-Python repository scripts remain subject to ordinary execution policy.
- Put the C# globals API signatures, file-access rules, limits, and examples in `docs/operations/csharp-scripting.md`, shipped through the existing `ThreadsmithDocs` bundle. Add only one instruction to `Tool-csharp_script-Description.md`: "Only if you elect to use csharp_script, first read its reference at {{CSharpScriptingReferencePath}}." Resolve that token to the installed reference, not the opened repository's docs folder. Reuse the existing read tool and packaged-documentation authority, extending exact-document access for ordinary parent/eligible child calls as needed. Do not preload the reference or duplicate its API details in stable prompts/tool descriptions; reuse an already-read reference while it remains in context.

## 7 Public Contracts

`run_process.workingDirectory` is optional and backward compatible. `csharp_script` requires active scratchpad storage, returns its saved snippet path, and exposes the scoped `Scratchpad` file API with authorized input reads and scratchpad-only writes. `tools:readFile:allowedFolders` grants external reads only; it grants no writes or cleanup authority. Scratchpad enablement and location remain unchanged; it grants storage access, not execution permission.

## 8 Project/File Changes

- `Threadsmith.Tools`: `WriteFileTool.cs`, `ToolPolicy.cs`, `BuiltInTools.cs`, `NewBuiltInTools.cs`; reuse `ProcessManager.cs` environment and cancellation support.
- `Threadsmith.Core` / existing policy configuration: declare external read-folder permissions and resolve them through the shared read policy.
- `Threadsmith.Scripting.Worker`: `Program.cs` and a small script-globals/file-access implementation; extend the existing worker request and semantic validation.
- `Threadsmith.App` / `Threadsmith.Execution`: existing scratchpad and session boundary coordination where ordering needs adjustment.
- Prompt assets: `System-Scratchpad.md`, `System-SystemPrompt.md`, `System-RepositoryInspection.md`, and existing writer/process/C# tool descriptions as needed.
- Documentation: `docs/operations/csharp-scripting.md`; prompt catalog/token declaration and tool rendering for `CSharpScriptingReferencePath`; existing packaged-documentation read policy integration.
- Existing writer, tool-policy, C# conversation, process, context/child prompt, and scratchpad lifecycle tests.

## 9 Ordered Tasks

1. Add invocation-aware script writing and focused containment tests.
2. Extend process working-directory support and scratchpad execution policy; retain existing execution gates.
3. Persist C# snippets, add scoped streaming file access, and use the same scratchpad execution settings through the existing worker.
4. Verify termination-before-cleanup ordering; briefly update appropriate prompts with C#-only scripting and the request to enable `csharp_script` when disabled; update documentation.
5. Run final validation and record results in this document.

## 10 Testing

Cover script writes inside/outside the scratchpad, inactive capability, traversal/links, overwrite/size checks, process directory/environment, unchanged execution denials, C# snippet retention on success/failure, timeout/cancellation, and cleanup after tracked work stops. Exercise ordinary parent and eligible child tool entry points. Verify Python script destinations are rejected by both writers. With `csharp_script` omitted from the tool inventory, verify parent/child context still instructs C#-only scripting and asking the user to enable the tool, with no alternate scripting fallback.

Through the real C# worker, aggregate a large scratchpad log incrementally and write results. Cover text/binary creation, subdirectories, explicit overwrite, append, incremental output, and read-back of generated data. Verify authorized repository/external reads, denied unconfigured reads, write-only export grants not granting reads, prohibited paths, child narrowing, policy rebind, and rejection of every non-scratchpad write. Cover lazy reads, write limits, cancellation/handle disposal, missing files, rejected path escapes and links, and continued rejection of direct `System.IO` access, including fully qualified names and aliases.

Verify the reference is packaged and readable through the advertised path from an unrelated repository for parent and eligible child calls. Requests that do not elect scripting include only the short description instruction, with no reference body. Keep documented signatures/examples synchronized with the implemented API.

Run final restore/build/solution tests and changed-file formatting/analyzers per `CONTRIBUTING.md`, plus `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`. Record exact commands and platform limits; use existing CI for cross-platform validation when submission is requested.

Validation (2026-10-31, Windows):

- `dotnet restore src/Threadsmith.sln` and `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` passed; final build: zero warnings/errors.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` passed: 4,824 passed, 58 skipped, zero failed. Enabled `THREADSMITH_SCRIPT_INTEGRATION=1`; `THREADSMITH_TEST_RIPGREP` pointed to `artifacts/test-ripgrep/stage/tools/rg.exe`, staged with `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep`.
- Real-worker verification read a 100,000-line scratchpad log, wrote its aggregate, read configured external input, wrote that result to the scratchpad, and denied the external read after grant removal. Direct filesystem and forged host-authority probes remained rejected.
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include $changedFiles` passed for implementation-owned C# changes and the authorized TUI test correction. Unrelated pending TUI source and semantic-notification test edits were excluded. `git diff --check` passed.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` passed (118 packaged files).
- Clean-context adversarial reviews and follow-up reviews found no remaining actionable issues. Fixes covered host API bypasses, shutdown joins, snippet limits, and link-check boundaries; existing external scratchpad capabilities remain compatible.
- macOS/Linux execution was not available. Windows link-creation tests skipped because the runner lacks symbolic-link privileges. Other skips retain their platform, explicit-test, or external-fixture prerequisites. The pending TUI progress test was corrected with user approval to match visible terminal text; all five cases passed.

## 11 Security/Permissions

Preserve path containment, link protection, capability revocation, execution approval, and child tool restrictions. Arbitrary shell commands and executed code can still access other locations under existing authority; enforcing confinement is future work.

## 12 Observability

Use existing tool activity and tracked process events. Expose saved script paths in ordinary results/provenance without echoing script bodies into new logs.

## 13 Migration/Compatibility

No new runtime dependency. External read folders default to empty; existing export grants retain their write-only meaning. Existing `run_process` callers retain repository-root execution. Users of `csharp_script` must enable the scratchpad and its repository ignore rule.

## 14 Acceptance Criteria

Temporary scripts can be written, inspected, and executed in the scratchpad through normal tools. C# can stream files from the scratchpad and effective configured read locations, and create, overwrite, or append text/binary intermediate data and results through the scoped API, including incremental output and subdirectories within the scratchpad only. Configured input folders are never modified or included in session cleanup. C# snippets and default temporary/cache files remain there until session cleanup. Tracked execution stops before cleanup. No script-extension allowance leaks into ordinary artifact folders, and storage enablement never bypasses execution policy. Extend Scenario SCRATCHPAD and MTP-274 for these behaviors.

## 15 Risks

Working-directory and environment defaults organize cooperative execution; they do not confine arbitrary code or guarantee removal of independently detached processes. The C# API and compiler checks are application restrictions, not an OS security boundary. Concurrent callers require unique script filenames. Future container sandboxing will own enforced filesystem/process isolation; broader shell enforcement remains deferred until that work.

## 16 Documentation

Update scratchpad/tool guidance, `docs/user-guide.md`, repository-permissions/security-policy descriptions, and prompt catalogs (`docs/operations/prompts.md`, `docs/prompt-file-reference.md`) for changed purposes or token meanings. Update Scenario SCRATCHPAD and MTP-274 during implementation. Leave completed milestone contracts unchanged.

Keep the detailed scripting reference solely in `docs/operations/csharp-scripting.md`; the tool description points to it for on-demand reading.

## 17 Open Decisions

None. Python scripting is prohibited for now. Containers are the chosen long-term sandboxing direction; implementation and broader `run_process` governance are deferred.
