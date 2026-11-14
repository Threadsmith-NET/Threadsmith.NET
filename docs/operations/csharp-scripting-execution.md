# C# scripting execution reference

Read this reference when using `csharp_script`; reuse it while it remains in context.

## Execution and results

- Use `kind: "expression"` for a value or `kind: "statement"` for multiple statements. The default is `expression`. Return a final expression without a trailing semicolon, or use `return` inside a statement block.
- `System` is imported automatically. Defaults also import and permit `System.Linq`, `System.Collections`, `System.Collections.Generic`, and `System.Text.Json`. Explicit `allowed_assemblies` configuration can restrict these APIs; imports do not grant permissions.
- Console access is prohibited. The returned value is converted with `.ToString()`; collections are not automatically serialized. Return readable text with `string.Join`, or explicitly serialize supported data with `JsonSerializer.Serialize`.
- Use `using (...) { ... }` to dispose resources. The current script compiler rejects top-level `using var` declarations.
- Each call runs in a fresh, single-use worker, which may already be prewarmed. Variables do not persist between calls. The host saves the submitted `.csx` in the session scratchpad and returns its path.
- Directives, direct file/process/network/reflection/native access, dynamic code, and unsafe constructs are prohibited. Follow the specific rejection message; do not repeat the same denied operation through another API.

## Files and limits

`Scratchpad` is supplied automatically. Relative paths resolve inside the scratchpad. For authorized repository inputs or trusted `tools:readFile:allowedFolders` grants, pass an absolute path. Prohibited paths, child scope, and link checks still apply; export write grants do not grant reads. Outputs must stay inside the scratchpad, which is cleared at session boundaries. Python script destinations are rejected.

| Method | Behavior |
|---|---|
| `IEnumerable<string> ReadLines(string path)` | Lazily read text lines; dispose enumeration when stopping early. |
| `IEnumerable<byte[]> ReadChunks(string path, int chunkSize = 65536)` | Lazily read independent byte chunks; chunk size is at most 1 MiB. |
| `Task WriteAllTextAsync(string path, string text, bool overwrite = false, CancellationToken cancellationToken = default)` | Atomically create UTF-8 text; replacement requires `overwrite: true`. |
| `Task WriteAllBytesAsync(string path, byte[] bytes, bool overwrite = false, CancellationToken cancellationToken = default)` | Atomically create binary output. |
| `Task AppendTextAsync(string path, string text, CancellationToken cancellationToken = default)` | Append UTF-8 output, creating the file if needed. |
| `Task AppendBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)` | Append binary chunks. Cancellation may leave a partial appended chunk. |
| `void CreateDirectory(string path)` | Create a scratchpad subdirectory. Writes also create parent directories. |


Omit cancellation arguments for normal scripts; the worker supplies its deadline token. Execution time, returned-output bytes, and cumulative file-write bytes are bounded by session configuration. Stream input and stop once the requested subset is found. Bound both record count and returned text; for larger output, append chunks to a scratchpad file and return its path with a short summary. Use `overwrite: true` only when replacement is intended.

If the tool or scratchpad is disabled, ask the user to enable it. These APIs exist inside `csharp_script`, not separately launched C# files. The checks are application policy, not an OS sandbox.

## Example: computation

Use `kind: "expression"`. The returned output is `5`.

```csharp
Math.Sqrt(25)
```

## Example: bounded JSONL subset

Use `kind: "statement"`. Given `events.jsonl` in the scratchpad, return the first 20 error messages, each limited to 200 characters. This example assumes valid JSON objects with string `level` and `message` properties. Replace the input path with an authorized absolute path when needed.

```csharp
var matches = new List<string>();
foreach (var line in Scratchpad.ReadLines("events.jsonl"))
{
    using (var document = JsonDocument.Parse(line))
    {
        var entry = document.RootElement;
        if (entry.GetProperty("level").GetString() == "error")
        {
            var message = entry.GetProperty("message").GetString() ?? string.Empty;
            matches.Add(message[..Math.Min(message.Length, 200)]);
        }
    }

    if (matches.Count == 20)
    {
        break;
    }
}
string.Join("\n", matches)
```
