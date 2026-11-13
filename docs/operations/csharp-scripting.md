# C# scripting

The optional `csharp_script` tool performs temporary computation and file analysis in a fresh tracked worker. Enable the tool and the session scratchpad before use; the scratchpad must be ignored by the repository's `.gitignore`.

For execution rules, available file methods, and tested examples, see the [scripting execution reference](csharp-scripting-execution.md). The tool directs the agent to that reference only when it chooses to use scripting.

## Configuration and lifecycle

Configure `tools:config:csharp_script:allowed_assemblies`, `timeout_ms`, and `max_output_bytes` through the existing [security policy](security-policy.md). The default APIs include LINQ, collections, and JSON; explicit allowlists replace those defaults. See the [configuration example](../user-guide.md) in the user guide.

The host saves each submitted snippet as a `.csx` file in `.threadsmith/.scratchpad` and returns its path. Files survive individual calls, but variables do not. Scratchpad files are deleted at session boundaries and application startup/shutdown. All script outputs stay in the scratchpad. Absolute input paths require repository authority or trusted `tools:readFile:allowedFolders` grants.

If a tracked process cannot be confirmed terminated within the shutdown backstop, cleanup stays blocked to preserve its working files. Restart Threadsmith before retrying cleanup.

## View and copy submitted source

In the interactive terminal, each retained script completion has a collapsed **C# script** control. Click the heading to expand it. The preview shows at most 20 logical lines and 8,192 source characters. A notice identifies a truncated preview. If the normalized preview cannot fit transcript retention, the header says **Preview omitted: retention limits**. Under very small budgets, **[!] C# script** gives the same omission warning without adding rows or bytes to the collapsed controls. The full-source action still works. Execution output and errors remain outside the preview.

**View full script…** stays available while the preview is collapsed. Click it, or use output focus with **Ctrl+Up/Down** to select a control and **Enter/Space** to activate it. The standard read-only modal shows the complete retained submitted source, including scripts longer than normal scrollback. Scroll with arrows or Page Up/Down. Select with the mouse or Shift+arrows. **F6** copies the selection; **F4** copies the full script. **Esc** closes the modal and restores the underlying input focus and draft.

Copy uses the original source, including tabs, trailing spaces, line endings, and Unicode. Display text is terminal-safe and can differ from the copied text. The existing clipboard policy still applies (65,536 UTF-8 bytes by default). A multibyte script can fit the 65,536-character source limit but exceed the clipboard byte limit. The modal reports a blocked or failed copy; it does not silently truncate or bypass that limit. A successful write means the OSC 52 sequence was sent; the terminal must permit clipboard writes.

Both views use TUIKit's lightweight C# highlighting. Multiline comments, raw/interpolated strings, and newer keywords can have imperfect coloring. Unsupported or failed highlighting falls back to readable plain text and never changes copied source.

Source is a bounded, live-only attachment to its own invocation, including compilation and runtime failure results after capture. It is not added to model output, routine logs, or persisted result data. Source shares transcript retention; a source that cannot fit shows an unavailable notice. Eviction, session replacement, and closing retained child views release access. Restored sessions do not restore tool-detail source: older output still renders without an active source action. The viewer never reopens the scratchpad path or reruns a script. Headless behavior is unchanged.
