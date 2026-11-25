# C# scripting

The optional `csharp_script` tool performs temporary computation and file analysis in a fresh tracked worker. Enable the tool and the session scratchpad before use; the scratchpad must be ignored by the repository's `.gitignore`.

For execution rules, available file methods, and tested examples, see the [scripting execution reference](csharp-scripting-execution.md). The tool directs the agent to that reference only when it chooses to use scripting.

## Configuration and lifecycle

Configure `tools:config:csharp_script:allowed_assemblies`, `assembly_config_action`, `timeout_ms`, and `max_output_bytes` through the existing [security policy](security-policy.md). The default APIs include LINQ, collections, text helpers such as `StringBuilder` and `Encoding`, regular expressions, JSON, and SHA-256 hashing of byte arrays. Listing `System.Text` alone does not allow its child namespaces.

- `merge` (default) adds the configured comma-separated names to the built-in list and removes exact duplicates.
- `override` uses only the configured names. An empty `allowed_assemblies` string clears the optional list; omitting `allowed_assemblies` keeps the defaults if no trusted layer supplies a list.

Trusted configuration precedence selects one list and one action; lists from different sources are not combined. Machine-enforced `allowed_assemblies` still narrows the result, and worker restrictions still apply. If you relied on the previous replacement behavior, set `assembly_config_action` to `override`. See the [configuration example](../user-guide.md) in the user guide.

There is no assembly-count cap. The former `max_assemblies` setting is ignored and can be removed from existing configuration. Limits on the length of each name and the total setting still apply; see [scripting resource limits](resource-limits.md#toolsconfigcsharp_script).

The host prepares one unused worker in the background at startup by default. Set `tools:config:csharp_script:NumberOfWarmWorkersInPool` to change that count; `0` disables the pool and starts a fresh worker for each invocation. Set `tools:config:csharp_script:WorkerCreationTimeOut` to change the creation/acquisition timeout in milliseconds (default `3000`). These are ordinary startup preferences, not permission grants; restart the application after changing them.

Warm workers initialize the Roslyn compiler without executing a script or receiving file authority. Each worker serves exactly one invocation and exits; variables and script state never carry between calls. Taking a worker queues background replacements on the thread pool before script execution, without waiting for those replacements. If no worker is ready, the invocation asynchronously waits up to the creation timeout. Idle workers have no expiry and remain available until application shutdown. Script execution still uses the ordinary tracked-process path, invocation-specific file scope, output limits, and cancellation.

The host saves each submitted snippet as a `.csx` file in `.threadsmith/.scratchpad` and returns its path. Files survive individual calls, but variables do not. Scratchpad files are deleted at session boundaries and application startup/shutdown. All script outputs stay in the scratchpad. Absolute input paths require repository authority or trusted `tools:readFile:allowedFolders` grants.

If a tracked process cannot be confirmed terminated within the shutdown backstop, cleanup stays blocked to preserve its working files. Restart Threadsmith before retrying cleanup.

Scripts can call eligible read-only tools through the host, not directly through the compiler or filesystem. The [execution reference](csharp-scripting-execution.md#calling-host-tools) describes discovery, arguments, limits, and failures. The default execution budget is 30 seconds for the entire script, including nested tool calls; a shorter policy still applies.

## View and copy submitted source

In the interactive terminal, each retained script completion has a collapsed **C# script** control inside its `TOOLS: csharp_script` output block. Click the heading to expand it. The preview shows at most 20 logical lines and 8,192 source characters. A notice identifies a truncated preview. If the normalized preview cannot fit transcript retention, the header says **Preview omitted: retention limits**. Under very small budgets, **[!] C# script** gives the same omission warning without adding rows or bytes to the collapsed controls. The full-source action still works. Execution output and errors remain outside the preview.

**View full script…** stays available while the preview is collapsed. Click it, or use output focus with **Ctrl+Up/Down** to select a control and **Enter/Space** to activate it. The standard read-only modal shows the complete retained submitted source, including scripts longer than normal scrollback. Scroll with arrows or Page Up/Down. Select with the mouse or Shift+arrows. **F6** copies the selection; **F4** copies the full script. **Esc** closes the modal and restores the underlying input focus and draft.

Copy uses the original source, including tabs, trailing spaces, line endings, and Unicode. Display text is terminal-safe and can differ from the copied text. The existing clipboard policy still applies (65,536 UTF-8 bytes by default). A multibyte script can fit the 65,536-character source limit but exceed the clipboard byte limit. The modal reports a blocked or failed copy; it does not silently truncate or bypass that limit. A successful write means the OSC 52 sequence was sent; the terminal must permit clipboard writes.

Both views use TUIKit's lightweight C# highlighting. Multiline comments, raw/interpolated strings, and newer keywords can have imperfect coloring. Unsupported or failed highlighting falls back to readable plain text and never changes copied source.

Source is a bounded, live-only attachment to its own invocation, including compilation and runtime failure results after capture. It is not added to model output, routine logs, or persisted result data. Source shares transcript retention; a source that cannot fit shows an unavailable notice. Eviction, session replacement, and closing retained child views release access. Restored sessions do not restore tool-detail source: older output still renders without an active source action. The viewer never reopens the scratchpad path or reruns a script. Headless behavior is unchanged.
