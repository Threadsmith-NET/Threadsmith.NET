# Implementation plan: Run C# scripts from files

**Status:** Draft for review. Implementation has not started.
**Delivery track:** Maintenance extension to the existing scripting capability.
**Prerequisites:** Keep the current scratchpad, tool policy, worker process, and source-detail contracts. Read root `AGENTS.md`, planning governance, and the C# guardrails before implementation.
**Writing style:** Simplified Technical English. Use short instructions and defined terms.

## 1. Objective

Let `csharp_script` accept inline code or the name of a `.csx` file. Let the model run an existing script without sending its source again. Permit scripts stored in the repository. Keep the existing execution and permission checks.

This draft is in `.inbox` for review. It does not change milestone status. If adopted into the planning package, add its navigation entry and follow the planning governance rules.

## 2. Architectural context

The host must own file access, tool permission, process lifetime, and result publication. File input changes how the host gets source text. It must not create a second execution path.

Use these existing components:

- `CSharpScriptTool` for tool input and validation.
- `ScriptFilePolicy`, `ScopedFilePaths`, and `RepositoryPathPolicy` for file authority and path checks.
- `ToolInvocationPipeline` for admission, source detail, execution, and completion.
- `CSharpScriptEngine` for source capture and worker execution.
- The existing scripting worker for compilation and script checks.

Do not use shell execution to run a `.csx` file. Do not add a second worker or compiler service.

## 3. Scope

- Accept exactly one source input: `code` or `scriptPath`.
- Resolve a bare file name in the active session scratchpad.
- Resolve a relative path with directory information from the repository root.
- Accept an absolute path only when it is inside the repository.
- Require read permission for the selected file.
- Read the file once into a bounded source snapshot.
- Use that same snapshot for source detail, the saved execution copy, and worker execution.
- Update tool instructions, reference documents, and tests.

## 4. Non-scope

- No script variables or runtime state shared between calls.
- No compiled-script cache.
- No automatic rerun, watch mode, or new retry behavior.
- No execution from external read-folder grants.
- No `#load`, `#r`, script arguments, or changes to allowed assemblies.
- No new repository write authority for scripts.
- No changes to scratchpad cleanup.

## 5. Current state

`CSharpScriptInput` requires `Code`. It also accepts `Kind` and `Concepts`. `Kind` defaults to `expression`.

`CSharpScriptEngine` saves the submitted code as a unique `.csx` file in the scratchpad. It sends the code string to a worker through standard input. The worker compiles this string. It does not execute the saved file by path.

`CSharpScriptOutput.ScriptPath` identifies the saved execution copy. Failed worker results also include this path.

`ToolInvocationPipeline` reads `script.Code` directly to create `ToolSourceDetail`. Its current comment requires the same immutable string to reach the engine. A nullable `Code` property alone will break this behavior. File input must join this source-capture path.

The current file-read helper can accept external approved folders. Script source needs an additional repository-containment check. External read permission must not permit external script execution.

## 6. Proposed design

### 6.1 Input selection

Add optional `scriptPath`. Make `code` optional in the input record and tool schema. Require exactly one non-null source field. Reject empty or whitespace-only values. Reject a request that supplies both fields, even when one is empty. Reject a request that supplies neither.

Keep `kind`, its valid values, and its default. A file does not store the requested kind. A caller must send `kind: "statement"` when required, including on a rerun.

### 6.2 Path rules

The word **scratchpad** means the active session directory at `<repository>/.threadsmith/.scratchpad`. Do not create a new root-level `.scratchpad` directory.

| Input | Resolution |
|---|---|
| `saved.csx` | `<scratchpad>/saved.csx` |
| `./saved.csx` | `<repository>/saved.csx` |
| `scripts/check.csx` | `<repository>/scripts/check.csx` |
| `.threadsmith/.scratchpad/saved.csx` | The named file under the repository root |
| Absolute path inside the repository | The named file, subject to normal read checks |
| Absolute path outside the repository | Reject |
| Relative path that escapes the repository | Reject |

A bare name has no directory separator and is not rooted. Treat explicit `./` as directory information. Do not search the repository when a bare name is absent from the scratchpad. This rule prevents ambiguous selection.

Use the repository path helpers for normalization, containment, and case handling. Normalize `..` before testing containment. Do not use string-prefix checks. Handle the path separators supported by the host. Document the accepted forms on Windows, macOS, and Linux.

Reject ambiguous drive-relative paths, device paths, alternate data streams, and non-file URI inputs. Require a regular file with a `.csx` extension. Reject symbolic links, junctions, and reparse points according to the existing file policy, including linked parent directories. Preserve prohibited paths and narrowed child-agent read scope.

### 6.3 Source preparation

Add a small preparation step to the existing scripting path. Prefer an existing preparation pattern if one fits. Do not add a general framework for this single input change.

Run normal tool admission and policy checks before file I/O. Then resolve and validate the source path. Revalidate policy before execution as the existing pipeline requires.

Read with cancellation and a strict byte bound. Apply the existing code-character limit to the decoded text. Keep the existing saved-file byte limit. Do not read an entire oversized file before rejecting it. Define UTF-8 input, with an optional UTF-8 byte-order mark. Reject invalid encoding rather than changing source text silently.

Capture the source in an immutable host-owned value. Include the source text and optional normalized source path. Pass that value to source-detail capture and the existing engine call. Keep it out of model-supplied input authority. Do not reread the path for the worker or for the UI.

Use existing safe-read mechanisms to address path replacement during access. Do not claim that path checks provide an OS sandbox. If the file changes after a successful capture, execute the captured text. A later invocation reads the file again. If access cannot establish an authorized readable file, fail before worker execution.

### 6.4 Execution and saved copies

Keep the current engine behavior. Save a unique execution copy for each accepted invocation, including file-based invocations. Execute the captured code through the current worker. Do not overwrite the original repository script.

Keep `ScriptPath` as the path of that execution copy. This copy records the text supplied to the worker, even if the original file later changes. Return the selected source file as additional file provenance where supported. Do not change the meaning of `ScriptPath` to sometimes mean the original source file.

The caller can use the returned `ScriptPath` to repeat the captured script. The caller can use the original repository path to run its current contents. Both calls compile and run again in a fresh, single-use worker. Keep non-idempotent tool classification.

## 7. Public contracts

Examples:

```json
{"code":"1 + 1","kind":"expression"}
```

```json
{"scriptPath":"script-previous-id.csx","kind":"expression"}
```

```json
{"scriptPath":"scripts/check-data.csx","kind":"statement"}
```

The generated schema must no longer require `code` unconditionally. Describe the exclusive choice in the schema and tool description. Enforce it in host validation even when a model provider cannot express the choice in its schema format. Test schema generation and runtime validation together.

Keep the output contract and inline-code behavior compatible. Missing files, invalid paths, unsupported encoding, excess size, and denied reads must produce clear bounded errors. Do not launch a worker for these failures.

## 8. Project and file changes

| File or area | Change |
|---|---|
| `src/Threadsmith.Tools/NewBuiltInTools.cs` | Input contract, exclusive-choice validation, source preparation integration, and provenance |
| `src/Threadsmith.Tools/ScriptFilePolicy.cs` | Dedicated script-source resolution using existing path checks |
| `src/Threadsmith.Tools/ToolInvocationPipeline.cs` | Capture prepared source text for inline and file input |
| Existing schema and tool preparation helpers | Extend only where needed; preserve other tools |
| `src/Threadsmith.Tools/Prompts/Tool-csharp_script-Description.md` | Explain both input forms, path rules, and rerun behavior |
| `tests/Threadsmith.ModelTooling.Tests` | Extend scripting, pipeline, source-detail, and schema tests |
| Existing child-tool policy tests | Verify narrowed read scope and inherited permissions |
| Scripting and prompt documentation | Describe source selection and exact execution-copy semantics |

No worker protocol change should be necessary. The worker still receives code text.

## 9. Ordered tasks

1. Read the applicable instructions and existing schema, policy, safe-read, and source-detail tests. Trace parent and child calls through the tool pipeline.
2. Add the input contract and exclusive-choice validation. Keep existing inline requests valid.
3. Add file resolution and bounded source capture. Reuse current path policy helpers.
4. Connect the captured source to source detail and the engine. Remove direct assumptions that `input.Code` is always present.
5. Keep one worker execution path and existing saved-copy behavior. Add original-file provenance without changing `ScriptPath` semantics.
6. Add the focused tests below. Fix failures through the real entry points.
7. Update the tool prompt, references, and applicable acceptance/manual scenarios.
8. Review the full change against path authority, exact-source evidence, cancellation, and compatibility. Run final validation after corrections are complete.

## 10. Testing

Use deterministic tests. Use barriers or explicit completion signals for source-change and cancellation tests. Do not use sleeps or retry-until-green loops.

Test these cases:

- Inline expression and statement requests still work.
- Both source fields, neither field, null-only input, and blank values fail correctly.
- Bare name, explicit root-relative file, nested relative path, and in-repository absolute path resolve correctly.
- A missing bare name does not fall back to a repository file with the same name.
- External absolute paths and traversal escapes fail, even with an external read-folder grant.
- Prohibited paths, narrowed child scope, links, and platform-specific unsafe path forms fail.
- Wrong extension, directory input, unreadable file, invalid UTF-8, and byte/character limits fail before worker execution.
- The same captured text reaches source detail, the saved copy, and the worker. Changing the original after capture does not change the current execution.
- Source-detail retention limits still apply. Large source is not retained through a second unbounded field.
- A saved inline script can be run again by its returned absolute path or bare filename.
- A repository script remains unchanged. Each invocation returns its own saved execution copy.
- Compilation errors and timeouts retain the existing saved-copy behavior.
- Cancellation propagates through file reading and worker execution.
- Disabled tool, disabled scratchpad, and insufficient trust retain current behavior.
- Headless, interactive, and child execution use the same policy and source preparation.

During implementation, batch related edits. Run focused tests when they resolve a concrete uncertainty. Diagnose failures and rerun affected tests before broad validation. Reserve the full suite for the final state unless repository rules or a specific risk require it earlier. Reuse results when relevant inputs are unchanged.

For final implementation validation, follow `CONTRIBUTING.md` and current CI commands. Run applicable architecture, schema, prompt, and documentation checks. Record exact commands and results. Do not report untested platforms as passing.

## 11. Security and permissions

File existence does not grant execution authority. Keep current tool trust, enablement, approval, child scope, and worker restrictions. Require an active scratchpad for both source forms.

Repository script source is executable input. Do not auto-discover or execute it during repository opening. Read it only for an authorized tool invocation. Do not allow an external read grant to widen the script-source boundary.

Keep script writes in the scratchpad. Selecting a repository script must not grant permission to edit repository files. Keep directive restrictions and assembly limits unchanged.

## 12. Observability

Use the existing tool activity, result, error, and source-detail paths. Report the actual captured source. Do not display current file contents as proof of what an earlier invocation ran.

Keep source text bounded and subject to existing retention rules. Do not add full source to ordinary logs. Include file provenance without exposing denied external content.

## 13. Migration and compatibility

Existing callers that supply `code` require no changes. Update direct property accesses and test doubles for optional code. Do not silently infer a path from the `code` field.

No persistent-state migration is expected. Keep the output field meaning unchanged. Verify that schema consumers and provider adapters accept the new optional input field.

## 14. Acceptance criteria

1. A model can run inline code, then repeat it with the returned file name without sending source again.
2. A model can run a checked-in `.csx` file by repository-relative path.
3. Absolute paths work only inside the repository and within current read authority.
4. Exactly one source input is required.
5. Source detail and the worker use the same captured text.
6. File input preserves all current worker and script restrictions.
7. The original repository script is not changed.
8. Tests cover path boundaries and both input forms through the real tool pipeline.

## 15. Risks

- A path-only implementation could display one source version and execute another. Capture once.
- Reusing a general external-read helper alone could permit external scripts. Add explicit repository containment.
- Nullable code could break source-detail capture or generated schemas. Review all consumers.
- A bare name could select the wrong file if fallback search is added. Use the fixed resolution rules.
- Large or changing files could bypass bounds. Bound the actual read and validate the captured text.
- File-based invocations continue to create saved copies. This preserves exact-source evidence and uses the existing session cleanup policy.

## 16. Documentation

Update `docs/operations/csharp-scripting-execution.md` with examples and path rules. Update `docs/operations/tools.md` where it describes the input contract. Update `docs/operations/prompts.md` and `docs/prompt-file-reference.md` for the changed tool argument instructions. Keep the existing prompt filename and token contract unless a separate need is found.

Update the owning acceptance scenario and manual scripting procedure for the new observable behavior. Do not reopen completed milestone contracts or change milestone status for this draft.

Suggested tool description text:

> Supply exactly one of code or scriptPath. To run a saved script again, supply its returned ScriptPath or its bare file name. A bare file name resolves in the active scratchpad. A relative path with directory information resolves from the repository root. Use ./name.csx for a file at the repository root. Absolute paths must stay inside the repository. Normal read permissions apply. Each call runs in a fresh worker. Variables and previous results are not retained. Supply the required kind again.

## 17. Open decisions

No user decision is required before implementation. This plan selects `scriptPath`, permits in-repository absolute paths, uses explicit `./` for repository-root files, and preserves a saved execution copy for every call.

The implementer must choose the smallest source-preparation extension after inspecting existing pipeline patterns. That choice must preserve one source snapshot and one execution path.
