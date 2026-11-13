# C# scripting

Use `csharp_script` for temporary computation and file analysis. If it is disabled, ask the user to enable it. Python and alternate scripting interpreters are unavailable. The session scratchpad must be enabled and ignored by the repository's `.gitignore`.

The host saves each submitted snippet as a `.csx` file in `.threadsmith/.scratchpad` and returns its path. Files survive individual invocations but are deleted at session boundaries and application startup/shutdown. `kind` accepts `expression` or `statement`; scripts can return their result as their final expression. Async file methods can be awaited.

## View and copy submitted source

In the interactive terminal, each retained script completion has a collapsed **C# script** control. Click the heading to expand it. The preview shows at most 20 logical lines and 8,192 source characters. A notice identifies a truncated preview. If the normalized preview cannot fit transcript retention, the header says **Preview omitted: retention limits**. Under very small budgets, **[!] C# script** gives the same omission warning without adding rows or bytes to the collapsed controls. The full-source action still works. Execution output and errors remain outside the preview.

**View full script…** stays available while the preview is collapsed. Click it, or use output focus with **Ctrl+Up/Down** to select a control and **Enter/Space** to activate it. The standard read-only modal shows the complete retained submitted source, including scripts longer than normal scrollback. Scroll with arrows or Page Up/Down. Select with the mouse or Shift+arrows. **F6** copies the selection; **F4** copies the full script. **Esc** closes the modal and restores the underlying input focus and draft.

Copy uses the original source, including tabs, trailing spaces, line endings, and Unicode. Display text is terminal-safe and can differ from the copied text. The existing clipboard policy still applies (65,536 UTF-8 bytes by default). A multibyte script can fit the 65,536-character source limit but exceed the clipboard byte limit. The modal reports a blocked or failed copy; it does not silently truncate or bypass that limit. A successful write means the OSC 52 sequence was sent; the terminal must permit clipboard writes.

Both views use TUIKit's lightweight C# highlighting. Multiline comments, raw/interpolated strings, and newer keywords can have imperfect coloring. Unsupported or failed highlighting falls back to readable plain text and never changes copied source.

Source is a bounded, live-only attachment to its own invocation, including compilation and runtime failure results after capture. It is not added to model output, routine logs, or persisted result data. Source shares transcript retention; a source that cannot fit shows an unavailable notice. Eviction, session replacement, and closing retained child views release access. Restored sessions do not restore tool-detail source: older output still renders without an active source action. The viewer never reopens the scratchpad path or reruns a script. Headless behavior is unchanged.

## Files

`Scratchpad` is supplied automatically as a script global. No initialization or imports are needed. Relative paths resolve inside the scratchpad. Absolute input paths may refer to authorized repository files or folders granted by trusted `tools:readFile:allowedFolders` configuration. Prohibited paths, child assignment scope, and link checks still apply. Export-folder write grants do not grant reads.

All outputs must stay inside the scratchpad. Python script destinations are rejected. Direct `System.IO`, reflection, processes, networking, dynamic code, and native interop remain prohibited. These application checks are not an OS sandbox.

| Method | Behavior |
|---|---|
| `IEnumerable<string> ReadLines(string path)` | Lazily read text lines; dispose enumeration when stopping early. |
| `IEnumerable<byte[]> ReadChunks(string path, int chunkSize = 65536)` | Lazily read independent byte chunks; chunk size is at most 1 MiB. |
| `Task WriteAllTextAsync(string path, string text, bool overwrite = false, CancellationToken cancellationToken = default)` | Atomically create UTF-8 text; replacement requires `overwrite: true`. |
| `Task WriteAllBytesAsync(string path, byte[] bytes, bool overwrite = false, CancellationToken cancellationToken = default)` | Atomically create binary output. |
| `Task AppendTextAsync(string path, string text, CancellationToken cancellationToken = default)` | Append UTF-8 output, creating the file if needed. |
| `Task AppendBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)` | Append binary chunks. Cancellation may leave a partial appended chunk. |
| `void CreateDirectory(string path)` | Create a scratchpad subdirectory. Writes also create parent directories. |

Omit cancellation arguments for normal scripts; the worker supplies its deadline token. The host also terminates workers on cancellation or timeout. The configured script timeout and returned-output limits still apply. Total bytes written per invocation are bounded by the host's file-write limit. Stream inputs and append small output chunks instead of buffering entire files. Write limits apply cumulatively, including overwritten data.

## Example

Given `input.log` in the scratchpad, count matching lines and save a compact result:

```csharp
var count = Scratchpad.ReadLines("input.log")
    .Count(line => line.Contains("ERROR"));
await Scratchpad.WriteAllTextAsync("results/error-count.txt", count.ToString());
count
```

For an authorized input outside the scratchpad, pass its absolute path to `ReadLines` or `ReadChunks`; keep the output path relative. Use `overwrite: true` only when replacement is intended. These globals exist inside `csharp_script`, not independently launched `.cs` files.

If a tracked process cannot be confirmed terminated within the shutdown backstop, cleanup stays blocked to preserve its working files. Restart Threadsmith before retrying cleanup; this exceptional state does not create a second process lifecycle.
