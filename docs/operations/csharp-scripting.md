# C# scripting

Use `csharp_script` for temporary computation and file analysis. If it is disabled, ask the user to enable it. Python and alternate scripting interpreters are unavailable. The session scratchpad must be enabled and ignored by the repository's `.gitignore`.

The host saves each submitted snippet as a `.csx` file in `.threadsmith/.scratchpad` and returns its path. Files survive individual invocations but are deleted at session boundaries and application startup/shutdown. `kind` accepts `expression` or `statement`; scripts can return their result as their final expression. Async file methods can be awaited.

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
