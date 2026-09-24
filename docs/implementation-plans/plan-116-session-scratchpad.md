# Plan 116 — Session-scoped scratchpad

**Status:** Implemented — 2026-09-24
**Delivery track:** Maintenance — configuration, prompt context, file-tool authority, and session lifecycle
**Prerequisites:** Existing layered configuration and repository rebinding; deployed prompt-asset catalog; `ToolInvocationContext`, `DefaultPolicyEngine`, `ToolPathRules`, and the built-in `search`, `read_file`, and `write_file` paths; serialized `/new` transitions; frontend-neutral startup warnings.

## 1. Objective

Add one optional scratchpad directory that gives the model a known place for temporary text files, intermediate test results, and disposable working notes.

The scratchpad is disabled by default. When safely configured and `write_file` is available for the session, the host advertises its resolved path in system context and grants only `search`, `read_file`, and `write_file` access to it. Writes inside the scratchpad require no user approval and work at every repository trust level. The host clears every entry beneath the scratchpad root at application startup, graceful shutdown, and before `/new` activates a fresh session.

The scratchpad is transient session infrastructure, not evidence, persistence, an artifact store, a mutation workspace, or a security sandbox.

## 2. Architectural Context

`ConfigurationBootstrap` builds the ordinary configuration hierarchy. Repository and session configuration are untrusted data. `HostFoundation` composes built-in tools and `WriteFileConfiguration`; `ToolStateManager` rebinds repository-owned settings. `DefaultPolicyEngine` and `ToolPathRules` own tool admission and path confinement. `SessionLifecycleApplication` serializes `/new` at a safe transition boundary. `ApplicationComposition` owns shutdown ordering, while `StartupDisplayWarnings` already carries bounded host-authored warnings to the interactive output window.

`ContextAssembler` builds the canonical model request from deployed prompt assets. Prompt wording belongs in the prompt catalog, while whether the scratchpad exists, its resolved path, and its authority remain code-owned facts.

The existing `write_file` tool has a general configured-folder grant, text-extension/content bounds, path checks, and ordinary trust/policy handling. Scratchpad support must extend this established tool and policy path. It must not create a second file writer or bypass the invocation pipeline.

## 3. Scope

- Add one nullable scalar configuration value, `scratchpad:path`, with a compiled default of `null`.
- Resolve relative values from the active repository root and accept fully qualified paths subject to layer trust and destructive-target validation.
- Allow normal application/machine, user, repository, session, CLI, and environment precedence while distinguishing absence from an explicit `null`; a higher-precedence explicit `null` disables a lower value.
- Confine values supplied by repository-owned repository or session configuration to the active repository, even when written with absolute syntax.
- Permit an external absolute directory only when selected by a repository-excluding application/machine, user, CLI, or environment layer.
- Create a missing in-repository scratchpad before checking its Git-ignore status.
- Disable the scratchpad for the current session when its resolved path is external and missing; do not create it, and show a startup warning.
- Warn at startup when an in-repository scratchpad is not covered by a repository-owned `.gitignore` rule or no applicable `.gitignore` exists.
- Clear the complete contents of every valid configured scratchpad at startup, graceful shutdown, and each `/new` transition.
- Advertise the scratchpad only when its session capability is active.
- Grant only `search`, `read_file`, and `write_file` path authority for the scratchpad. Only `write_file` receives write authority.
- Make scratchpad-targeted `write_file` calls approval-free at every trust level while retaining ordinary validation, limits, events, cancellation, and sanitization.
- Disable scratchpad capability for the session when `write_file` is absent or effectively disabled; leave configuration unchanged and do not inject scratchpad context.
- Document configuration, lifecycle, tool access, warnings, and the disabled default.

## 4. Non-Scope

- No general temporary-file API, shell working directory, process sandbox, compiler workspace, build output redirect, or persistence/artifact migration.
- No scratchpad access for `list_files`, semantic tools, Git tools, mutation tools, validation tools, `run_process`, MCP tools, extensions, skills, or delegated children through a new implicit grant. A child receives scratchpad authority only if it runs through the same session tool-policy snapshot and is explicitly given one of the three eligible tools.
- No automatic addition to `.gitignore` and no mutation of repository files during activation.
- No creation of a missing external directory.
- No preservation, recovery, archival, upload, diffing, or confirmation prompt before cleanup.
- No relaxation of `write_file` content-size, supported text-extension, overwrite, reserved-name, or atomic-write rules. This plan adds a path-scoped authority grant, not a more capable writer.
- No claim that Threadsmith is an operating-system filesystem sandbox. Other tools retain their existing authority, but receive no scratchpad-specific exception.
- No cleanup of the scratchpad root itself; cleanup removes every child entry and leaves the root available.

## 5. Current State

### 5.1 Configuration

The ordinary hierarchy is compiled defaults, machine, user, repository, session, CLI, and environment. `ConfigurationBootstrap.BuildTrusted` excludes repository-owned layers for privileged settings. Some existing configuration readers, including `WriteFileConfiguration`, walk providers to preserve replacing and explicit-empty semantics.

There is no scratchpad setting or provider-aware nullable scalar resolver. A direct `configuration["scratchpad:path"]` lookup cannot by itself express all required provenance rules, particularly explicit-null override and repository-layer confinement of external paths.

### 5.2 File tools and policy

`search` and `read_file` use the ordinary repository path rules. `write_file` uses `WriteFileConfiguration` as a separate compiled direct-write capability, then passes through the same policy and invocation pipeline. Its definition currently requires `TrustedRead`, although its declared approval is already `None`.

Changing the global `write_file` trust requirement would broaden every configured write folder. Scratchpad authority therefore has to be decided from the normalized destination and active session capability before the generic trust rejection, while ordinary destinations retain their current behavior.

### 5.3 Session and application lifecycle

`SessionLifecycleApplication` owns serialized new-session transitions. It currently has no transient filesystem participant. `ApplicationComposition.DisposeAsync` owns coordinated shutdown but has no scratchpad cleanup step. Startup warnings are assembled in application composition and rendered through the frontend-neutral interaction path.

### 5.4 Git-ignore inspection

There is no shared Git-ignore inspection service for this behavior. Text matching `.gitignore` is insufficient because negation, nested `.gitignore` files, escaping, and directory rules affect the result. Git's own `check-ignore -v --no-index` behavior is the authoritative mechanism when Git is available.

## 6. Proposed Design

### 6.1 Configuration and provenance

Add a host-owned `ScratchpadConfiguration` resolver with key `scratchpad:path`. It returns a detached `ScratchpadConfigurationValue` containing the raw presence state, normalized path, source trust category, and active repository identity. It must walk configuration providers in precedence order so these states remain distinct:

- key absent: continue to the next lower layer, ultimately yielding disabled;
- key explicitly `null`: stop and disable scratchpad;
- nonblank scalar: validate and resolve it;
- empty/whitespace, array/object, control characters, wildcard syntax, environment-variable syntax, or home-directory shorthand: invalid configuration, reported without interpreting or expanding it.

Relative paths resolve from the normalized active repository root at every layer. Fully qualified paths are normalized with platform comparison rules. Repository and repository-owned session values must resolve strictly beneath the repository root. A repository value resolving outside the repository is rejected and produces a bounded startup warning; it never falls back to a lower external value because the higher layer attempted an invalid override.

Application/machine, user, CLI, and environment values may resolve outside the repository. The effective configuration snapshot is rebound only at existing repository/session safe boundaries and is immutable for one active session.

### 6.2 Destructive-target validation

Before directory creation, inspection, cleanup, prompt injection, or tool authorization, validate the normalized root. Reject and disable any target that is:

- a filesystem root;
- the repository root or an ancestor of it;
- the user profile/home directory or an ancestor of it;
- the process/app base directory, machine/user Threadsmith configuration root, repository `.git`, repository `.threadsmith`, or an ancestor of any of those protected roots; the dedicated repository `.threadsmith/.scratchpad` subtree is permitted while other descendants of `.threadsmith` remain protected;
- equal to or nested beneath a configured prohibited path;
- a file rather than a directory;
- a symbolic link, junction, mount-point-like reparse target, or reached through a linked/reparse ancestor beneath the nearest trusted anchor; or
- ambiguous under Windows device-name, trailing-dot/space, alternate-stream, casing, or separator rules already enforced by file tools.

Validation must compare normalized path boundaries, not string prefixes. Diagnostics may identify the configured path but must not enumerate deleted filenames or file contents.

### 6.3 Activation order

One serialized `ScratchpadLifecycle` service owns activation and cleanup. Startup/repository activation follows this exact order:

1. Resolve the effective value and its provider trust.
2. If absent or explicitly null, publish the disabled snapshot and perform no filesystem or Git work.
3. Normalize and run destructive-target validation.
4. Determine whether the target is strictly inside the repository.
5. If it is inside and missing, create the directory.
6. If it is outside and missing, publish a session-disabled snapshot and queue a startup warning that the external scratchpad was not created.
7. For an existing or newly created in-repository directory, run the `.gitignore` check and queue a warning when needed.
8. Clear all directory contents using the cleanup algorithm in §6.4.
9. Capture effective `write_file` availability. If unavailable, publish a session-disabled capability and queue a startup message; configuration remains unchanged.
10. Otherwise publish one active immutable session snapshot used by prompts and eligible tools.

The `.gitignore` warning does not disable an otherwise valid in-repository scratchpad. Cleanup still occurs before the scratchpad becomes active.

### 6.4 Complete cleanup without traversal escapes

Cleanup deletes every file and subdirectory beneath the root, including hidden, read-only, dot-prefixed, temporary, and partially written entries. It removes read-only attributes where supported. It deletes symbolic links and junction entries themselves and never follows their targets. The root directory remains.

The service revalidates the root identity immediately before enumeration and again before destructive operations. It enumerates and deletes within one serialized lifecycle lease so `/new`, shutdown, and tool writes cannot overlap. Async boundaries accept and propagate `CancellationToken`; application shutdown receives a bounded cleanup backstop but does not silently report success when entries remain.

Startup cleanup failure disables scratchpad capability for the session and emits a warning. `/new` cleanup failure blocks activation of the new session, because otherwise files would cross the session boundary. Shutdown cleanup failure is reported through structured logs and the last available host presentation/error channel; the next startup retries cleanup before activation.

An external directory disabled because it was missing is never created or cleared later in that session. A valid configured root is still cleaned at startup and shutdown even if scratchpad capability is subsequently disabled because `write_file` is unavailable.

### 6.5 `.gitignore` check

Run the ignore check only for a root strictly inside the repository, after ensuring the directory exists. Use the existing governed process/Git execution substrate to invoke the installed Git executable with a bounded deadline and cancellation, equivalent to `git check-ignore -v --no-index -- <repository-relative-directory>/`.

The result counts as covered only when Git reports an effective matching source that is a repository-owned `.gitignore` file at the root or an applicable parent directory. A global excludes file or `.git/info/exclude` does not satisfy the requested `.gitignore` warning contract. A negated rule that leaves the directory visible produces a warning. Missing Git, command failure, missing applicable `.gitignore`, or an indeterminate result produces a conservative warning without disabling scratchpad capability.

Warnings are bounded, contain no directory contents, appear once per activation, and flow through `StartupDisplayWarnings` so the TUIKit output window displays them after startup. Headless startup writes the equivalent warning through its established warning/error surface.

### 6.6 Session capability snapshot

Add a host-owned immutable `ScratchpadSessionCapability` containing:

- active/disabled state and a closed disabled-reason enum;
- repository identity;
- normalized absolute root used for enforcement;
- model-facing path: slash-normalized repository-relative form for an in-repository root, otherwise the normalized absolute path;
- whether it is inside the repository; and
- activation generation/session identity.

Do not persist this capability as session memory or infer it from transcript text. The active snapshot is injected into `ToolInvocationContext` or an equivalent host-owned request context used by policy and path validation. It is replaced only at safe session/repository transitions.

### 6.7 Tool access and policy

Extend the established path-validation path with a closed access purpose: ordinary repository access or active scratchpad access. Only the exact built-in IDs `search`, `read_file`, and `write_file` may request scratchpad access. Configured wrappers may be unwrapped to those compiled tools; MCP and extension tools with the same display name or ID receive no grant.

For `search` and `read_file`, a normalized requested path beneath the active scratchpad may pass the scratchpad root check even when the root is external to ordinary approved repository roots. Their operations remain read-only and keep all existing result, size, sanitization, and cancellation bounds.

For `write_file`, policy evaluates the normalized destination before generic trust/approval admission:

- when the destination is beneath the active scratchpad, the required trust is `UntrustedInspection` and required approval is `None`;
- explicit global/tool denial and session-disabled tool availability still win and disable scratchpad activation rather than becoming an approval prompt;
- when the destination is outside the scratchpad, existing `TrustedRead`, configured allowed-folder, prohibited-path, and approval behavior remains unchanged.

The implementation must not lower `WriteFileTool.Definition.RequiredTrust` globally without adding an equivalent path-scoped policy requirement for non-scratch destinations. `write_file` still performs path validation before admission and immediately before directory creation, temporary-file creation, and atomic move. The scratchpad root becomes an additional session-scoped allowed destination; it does not need to be copied into `tools:writeFile:allowedFolders`.

No other tool receives the scratchpad root in approved roots, working directories, executable arguments, extension context, or MCP roots. Raw process tools keep their existing ambient operating-system limitations but receive no host authorization or prompt instruction to use the scratchpad.

### 6.8 Tool availability

Determine `write_file` availability from the same effective tool registry/policy snapshot that builds model-visible tools for the new session. It is unavailable when the compiled tool is absent, disabled, denied, removed by an allowlist, or otherwise excluded from that session's catalog. Ordinary trust alone does not make it unavailable when scratchpad capability can supply the narrower path-scoped trust grant.

If unavailable at activation, keep the configured lifecycle root for cleanup but set the session capability inactive, emit one startup message, omit scratchpad prompt context, and do not broaden `search` or `read_file`. Later `/tools` changes do not activate scratchpad mid-session; `/new` takes a fresh tool-availability snapshot.

If an active session later disables `write_file`, immediately revoke the scratchpad capability for future tool calls and prompt continuations. Re-enabling `write_file` does not reactivate it until `/new`, preventing a mid-session path-authority expansion.

### 6.9 Model context

Add a deployed prompt asset with one required path token and code-owned conditional insertion. Suggested model-facing wording:

> Scratchpad: `{ScratchpadPath}` is available through `search`, `read_file`, and `write_file` for temporary files, intermediate test results, and working notes. Treat it as session-scoped transient storage; its contents are deleted at session boundaries and application restart or shutdown.

Insert this as a stable host/system context section for the main model and eligible child model requests only while the session capability is active. Do not add an empty heading or disabled explanation when configuration is null, invalid, missing externally, or inactive because `write_file` is unavailable. The path and authority are host facts; prompt text cannot grant broader access.

Register the filename and token in `PromptFileNames`/`PromptAssetCatalog`, update publish synchronization, and update `docs/operations/prompts.md` plus `docs/prompt-file-reference.md` in the same implementation.

### 6.10 `/new` and shutdown

Inject the lifecycle service into the established session transition coordinator. For `/new`, acquire the existing safe transition boundary, revoke the old scratchpad capability, clear the configured root, then create/activate the destination session and capture a fresh capability. No new session event or prompt may become visible before successful cleanup.

Application composition owns final cleanup after active model/tool operations and child agents have stopped, but before the lifecycle service is disposed. Startup cleanup handles process crashes where graceful shutdown never ran. Repeated cleanup is idempotent.

## 7. Public Contracts

- Configuration key: `scratchpad:path`.
- Default: `null` (disabled).
- Relative path base: active repository root.
- Repository/session configuration: normalized result must remain strictly inside the active repository.
- Trusted application/machine, user, CLI, or environment configuration: may select a guarded existing external absolute directory.
- Missing in-repository directory: created before `.gitignore` inspection.
- Missing external directory: not created; scratchpad disabled for the session with a startup warning.
- In-repository directory without an effective repository `.gitignore` rule: remains active with a startup warning.
- Cleanup boundary: all child entries at startup, `/new`, and graceful shutdown; root retained.
- Eligible tools: `search`, `read_file`, `write_file`; only `write_file` may mutate.
- Scratchpad write authority: approval-free at every trust level, limited to the active normalized root.
- Null or inactive capability: no model-facing scratchpad context and no scratchpad path grant.

The capability and disabled-reason types are host-owned DTOs. No terminal, Git-library, configuration-provider, or filesystem-handle types cross public subsystem boundaries.

## 8. Project/File Changes

Expected implementation areas:

- `.threadsmith/config.example` — nullable key, hierarchy examples, trusted external-path restriction, warnings, and disabled default.
- `src/Threadsmith.App/ConfigurationBootstrap.cs`, `HostFoundation.cs`, `ApplicationComposition.cs`, `InteractiveFrontendRunner.cs` — configuration provenance, activation, startup warnings, composition, and shutdown ordering.
- `src/Threadsmith.Tools/` — scratchpad configuration/session capability/lifecycle contracts, safe cleanup, tool-path access purpose, and path-scoped policy handling.
- `src/Threadsmith.Tools/WriteFileConfiguration.cs`, `WriteFileTool.cs`, `ToolPolicy.cs`, `ToolStateManager.cs`, and built-in search/read implementations — reuse existing execution paths and apply the narrow scratchpad grant.
- `src/Threadsmith.Execution/SessionLifecycleApplication.cs` and composition wiring — serialized `/new` cleanup and capability refresh.
- `src/Threadsmith.Context/ContextAssembler.cs` and prompt catalog files — conditional system-context section.
- `src/Threadsmith.Context/Prompts/` — deployed scratchpad prompt asset.
- Relevant App bootstrap, ModelTooling, ConversationContext, RepositoryLifecycle, CoreRuntime, and Architecture tests.
- `docs/operations/configuration.md` or the current configuration owner, `docs/user-guide.md`, prompt documentation, acceptance scenarios, and manual test plan as required by planning governance.

Exact filenames may change after implementation inspection, but ownership and reuse boundaries above are binding.

## 9. Ordered Tasks

1. Read repository instructions, C# guardrails, configuration documentation, prompt-asset contracts, and current tool/session lifecycle implementations.
2. Add configuration provenance tests for absent, explicit null, relative, in-repository absolute, trusted external absolute, and rejected repository external values.
3. Implement normalized destructive-target validation and tests before writing cleanup code.
4. Implement serialized, non-following lifecycle cleanup with startup, `/new`, shutdown, cancellation, and failure tests.
5. Add create-before-ignore-check behavior and Git-backed ignore-source inspection with bounded warning results.
6. Compose startup activation and frontend-neutral warnings, including missing external and unavailable `write_file` cases.
7. Add the immutable session capability and thread it through the existing invocation context without adding it to general approved roots.
8. Extend `search` and `read_file` path validation for read-only scratchpad access.
9. Extend `write_file` and `DefaultPolicyEngine` with the destination-scoped no-approval/all-trust grant while preserving ordinary destinations.
10. Integrate capability revocation and cleanup into `/new`, tool-disable changes, repository rebinding, and shutdown ordering.
11. Add the deployed conditional system-context asset and synchronize its catalog, tests, publishing rules, and prompt documentation.
12. Update configuration/user/operations documentation and add governed acceptance/manual scenarios.
13. Run focused tests, solution build, architecture tests, prompt/release contracts, and an adversarial review through real startup, `/new`, tool, and shutdown entry points.

## 10. Testing

### 10.1 Configuration and path safety

- Default absent and explicit-null values produce no filesystem access, prompt text, or tool grant.
- Higher-precedence explicit null disables a lower configured value.
- Relative and absolute in-repository paths normalize identically.
- Repository/session values outside the repository fail closed with a startup warning; trusted user/application external values may activate only when the directory already exists.
- Filesystem roots, repository root/ancestors, home/config/app roots, `.git`, `.threadsmith`, prohibited paths, files, linked roots, linked ancestors, Windows device paths, and traversal attempts are rejected before deletion.
- Case and separator behavior is deterministic on Windows, Linux, and macOS.

### 10.2 Activation and warnings

- A missing in-repository directory is created before the ignore check.
- A missing external directory is not created, produces one output-window warning, remains configured, and is inactive for the session.
- Effective repository `.gitignore` rules, nested rules, negation, spaces, and escaped patterns are classified using Git output.
- Missing `.gitignore`, only global/info excludes, unavailable Git, and indeterminate Git checks warn without disabling a valid in-repository scratchpad.
- A missing/disabled/denied `write_file` produces an inactive session capability and no prompt injection.

### 10.3 Cleanup lifecycle

- Startup removes nested files/directories, hidden and read-only entries, stale atomic-write files, and link entries without touching link targets.
- `/new` clears before the new session becomes active and blocks transition when cleanup cannot complete.
- Graceful shutdown clears after active tool/model work stops.
- Simulated abnormal termination leaves files that the next startup clears before activation.
- Repeated cleanup is idempotent; concurrent writes, `/new`, and shutdown serialize without escaping the root.
- A cleanup failure reports remaining state without logging filenames or contents.

### 10.4 Tool authority

- At every trust level, `write_file` can create and overwrite an eligible text file beneath an active scratchpad without approval.
- The same low-trust call outside the scratchpad retains ordinary denial/approval behavior.
- Explicit tool denial disables capability rather than being bypassed.
- `search` and `read_file` can access external scratchpad content only while the capability is active.
- `list_files`, process, Git, semantic, mutation, validation, MCP, extension, and lookalike tool IDs receive no scratchpad path grant.
- Traversal, sibling-prefix, symlink/junction replacement, reserved paths, unsupported extensions, oversized content, and stale repository/session capabilities fail before I/O.
- Tool lifecycle events, activity, cancellation, output sanitization, and result limits remain on established paths.

### 10.5 Prompt and presentation

- The scratchpad line appears exactly once in canonical system context with the correct model-facing path only when active.
- Null, invalid, missing-external, and write-unavailable states omit the complete section.
- Child requests receive it only when their effective eligible tool set carries the same active capability.
- Prompt asset catalog, byte/token validation, six-RID publish synchronization, and prompt reference tests pass.
- Startup warnings render in the TUIKit output window and the headless warning channel without terminal types crossing interaction boundaries.

### 10.6 Validation commands

At minimum:

```powershell
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build
dotnet test tests/Threadsmith.RepositoryLifecycle.Tests/Threadsmith.RepositoryLifecycle.Tests.csproj --configuration Debug --no-build
dotnet test tests/Threadsmith.ConversationContext.Tests/Threadsmith.ConversationContext.Tests.csproj --configuration Debug --no-build
dotnet test tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build
dotnet test tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build
./eng/release/Test-ReleaseContracts.ps1
git diff --check
```

Use the repository's supported Microsoft.Testing.Platform filter syntax or run complete projects; a zero-test filtered run is not validation.

## 11. Security/Permissions

Scratchpad configuration is explicit consent to automatic deletion inside the validated root and approval-free `write_file` operations beneath it. That consent is path-scoped and does not authorize a repository to select an external destructive target.

Repository and session configuration are untrusted. They may select only a strict descendant of the active repository. External paths require a repository-excluding user-controlled layer and must already exist. Every destructive entry point repeats boundary and link checks. Cleanup never follows a symbolic link or junction, never targets the root itself, and never accepts broad protected roots.

The scratchpad grant does not override explicit tool denial, content/size/schema validation, reserved Threadsmith/Git locations, prohibited paths, secret handling, cancellation, or output sanitization. No approval token is synthesized or persisted. Policy records should state that the path matched the active scratchpad capability rather than falsely reporting general repository trust.

## 12. Observability

Emit structured, content-free lifecycle events or logs for resolution state, activation/disabled reason, inside/outside classification, directory creation, ignore-check outcome, cleanup start/completion/failure, `/new` cleanup, and shutdown cleanup. Record counts of removed files/directories and elapsed time, but not names or contents.

Tool invocations continue through ordinary started/completed/failed events. Add a bounded policy reason or flag indicating scratchpad authority was used. Startup warnings are host-authored presentation, not model text or durable repository memory.

## 13. Migration/Compatibility

The default is null, so existing installations perform no new filesystem work and receive unchanged prompts and tool policy. Existing `tools:writeFile:allowedFolders` behavior remains intact. Configuring the scratchpad does not rewrite that list.

No durable schema migration is required. Restored sessions capture current startup/session capability rather than trusting a previously persisted scratchpad path. Configuration changes require a new application/session activation boundary; they do not silently broaden a running session.

## 14. Acceptance Criteria

- `scratchpad:path` is documented, nullable, layered, relative-to-repository when not absolute, and disabled by default.
- Repository-owned configuration cannot select an external scratchpad.
- Missing in-repository roots are created before `.gitignore` inspection; missing external roots are not created and disable the session capability with a visible startup message.
- In-repository roots not covered by a repository `.gitignore` rule produce a visible startup warning.
- Every child entry is removed at startup, before `/new` activation, and at graceful shutdown without following links outside the root.
- A null/inactive scratchpad is absent from model context and grants no path access.
- An active scratchpad is described as session-scoped transient storage for temporary files, intermediate test results, and working notes.
- Only compiled `search`, `read_file`, and `write_file` paths receive scratchpad authority; only `write_file` may mutate it.
- Scratchpad writes need no approval and work at every trust level, while writes elsewhere preserve current policy.
- If `write_file` is unavailable, scratchpad capability stays disabled for that session without changing configuration.
- Existing file-tool, prompt, session, configuration, architecture, and release checks pass.
- An adversarial review finds no cleanup escape, repository-configured external deletion, policy-wide trust reduction, duplicate writer, prompt/config drift, or session-boundary leak.

## 15. Risks

- **Catastrophic deletion from an overbroad path.** Mitigate with trusted-layer provenance for external paths, protected-root rejection, strict-descendant checks, non-following cleanup, repeated validation, and destructive-boundary tests.
- **TOCTOU link replacement.** Serialize lifecycle/tool access, reject reparse points, revalidate immediately before I/O, delete links rather than targets, and prefer handle-relative/non-following platform APIs when available.
- **Global `write_file` privilege widening.** Keep the exception destination- and capability-scoped; verify ordinary destinations at low trust still fail.
- **Prompt claims that exceed actual tools.** Derive prompt insertion from the same immutable capability and effective tool snapshot used by policy.
- **Git-ignore false assurance.** Use Git's effective matcher and verify the matching source is a repository `.gitignore`; warn on uncertainty.
- **Shutdown interrupted before cleanup.** Treat startup cleanup as mandatory recovery before capability activation.
- **Repository/session transitions race cleanup.** Reuse the serialized transition boundary and lifecycle lease; do not add background deletion.
- **External directory disappearance.** Revalidate before every lifecycle operation and disable on the next activation rather than recreating it.

## 16. Documentation

Implementation must update:

- `.threadsmith/config.example` with `"scratchpad": { "path": null }`, precedence, relative-path rules, repository confinement, missing-directory behavior, cleanup semantics, and warnings;
- the current user/configuration guide with setup examples for an ignored repository scratchpad and an existing trusted external scratchpad;
- tool documentation explaining the three eligible tools and the path-scoped no-approval write grant;
- `docs/operations/prompts.md` and `docs/prompt-file-reference.md` for the conditional deployed prompt asset;
- acceptance scenarios for activation, warning, tool authority, and cleanup boundaries; and
- the manual test plan for startup, `/new`, graceful shutdown, crash recovery, `.gitignore`, and external-missing behavior.

No GitHub workflow, release packaging, or package-version change is expected unless prompt publish-contract implementation reveals an existing workflow assertion that must be synchronized.

## 17. Open Decisions

None. The following choices are binding for implementation:

- key: `scratchpad:path`;
- default and explicit null: disabled;
- repository/session external target: rejected and warned;
- trusted external target: must already exist or the session capability is disabled and warned;
- missing in-repository target: create before ignore inspection;
- unignored in-repository target: warn but remain active;
- cleanup: all child entries at startup, `/new`, and shutdown, with root retained and links never followed;
- `write_file` unavailable: capability disabled for the session, configuration unchanged;
- no mid-session reactivation after `write_file` becomes available; and
- existing `write_file` text and size constraints remain in force.

## 18. Implementation Evidence

- The solution builds with zero warnings and errors, and the complete automated test suite passes.
- Focused architecture, context-caching, repository-lifecycle, parallel-agent, and model-tooling coverage verifies configuration provenance, lifecycle cleanup, warning presentation, prompt provenance, exact built-in tool authority, low-trust approval-free writes, managed search fallback, repository rebinding, and disabled-tool behavior.
- Independent adversarial review iterated through repository rollback, configuration snapshot, prompt provenance, child tool-subset, link traversal, shutdown cleanup, warning delivery, and managed-search findings and finished clean after remediation.
- Windows junction regression cases now exercise linked in-repository ancestors and external scratchpad roots before cleanup. Symbolic-link-only cases may skip where the account cannot create symlinks; MTP-274 covers those platform-capability paths on a capable host.
- Plan 116 requires no GitHub workflow, release packaging, or package-version change; the deployed prompt catalog and public prompt/configuration documentation are synchronized in this change.
