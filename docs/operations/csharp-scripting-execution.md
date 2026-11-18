# C# scripting execution reference

Read this reference when using `csharp_script`; reuse it while it remains in context.

## Source selection and reruns

Supply exactly one non-null source field: `code` for inline source or `scriptPath` for a readable regular `.csx` file. Both fields, neither field, and empty or whitespace-only selected values fail. Both fields also fail when one is blank. `kind` defaults to `expression`; send `kind: "statement"` again for statement reruns.

| Input | Resolution |
|---|---|
| `saved.csx` | Active session scratchpad at `<repository>/.threadsmith/.scratchpad` |
| `./saved.csx` | Repository root |
| `scripts/check.csx` | Repository-relative file |
| `.threadsmith/.scratchpad/saved.csx` | Explicit repository-relative scratchpad file |
| Absolute path | Inside the repository and current read authority only |

A missing bare filename does not fall back to a repository file. Windows accepts `/` and `\` separators; macOS and Linux use `/`. Use `/` in portable relative paths. Traversal cannot escape the repository. Drive-relative, device, alternate data stream, and URI paths are rejected. Prohibited paths, narrowed child read scope, and link/junction/reparse-point checks still apply. External read-folder grants authorize data reads, not external script source.

File source must use strict UTF-8, with an optional UTF-8 BOM. Invalid encoding and excess byte/character limits fail before worker execution. The host captures the file once. For packaged skill scripts, it verifies the captured raw bytes against the host-bound package length and SHA-256, including any BOM, before exposing source evidence or starting execution. Unused scripts are not reread for every workflow step. Source detail, the saved execution copy, and the worker use that same captured text. Changes to the original after capture affect only later invocations.

`ScriptPath` always identifies a new scratchpad execution copy, including failed worker results. It never identifies the original repository source. Pass that returned path as `scriptPath`, or use its bare filename, to run the same captured source again during the session while the copy remains available. Scratchpad cleanup or session end removes the copy; it is not a durable script location. Run the original repository path to capture its current contents. The source file is not overwritten. Selecting it grants no repository write authority.

```json
{"code":"1 + 1","kind":"expression"}
```

```json
{"scriptPath":"script-previous-id.csx","kind":"expression"}
```

```json
{"scriptPath":"scripts/check-data.csx","kind":"statement"}
```

## Native skill scripts

Native skill packages can declare multiple integrity-checked `kind: "script"` assets at flat `scripts/<filename>.csx` paths. The procedure's host-context JSON supplies a `scripts` array with each script's `name` and repository-relative `scriptPath`; script bodies are not automatically injected into the prompt. Pass the supplied `scriptPath` to this same tool with the appropriate `kind`. Do not pass only the packaged filename: bare names select the scratchpad root, not the package's scripts folder.

Repository-contained package scripts use their repository-relative paths. For external packages, the host stages verified copies under `skill-scripts/<session-id>/<package-digest>/` in the active repository scratchpad when a procedure first runs. Discovery, verification, installation, and staging do not execute scripts. Every selected script's captured bytes must match its package length and SHA-256 before execution. A run returns a separate scratchpad execution copy in `ScriptPath`, with the same session-scoped rerun behavior described above.

Declare `csharp_script` as a required tool when a native skill needs it. Packaging scripts does not enable the tool by itself, grant authority, bypass this reference, or introduce another runner. Existing policy, read scope, source limits, cancellation, and worker restrictions still apply. See [native script authoring](../skill-authoring.md#optional-native-packaged-scripts) for manifest, staging, and integrity details, and [skills operations](skills.md#optional-native-packaged-scripts) for runtime behavior.

## Execution and results

- Use `kind: "expression"` for a value or `kind: "statement"` for multiple statements. The default is `expression`. Return a final expression without a trailing semicolon, or use `return` inside a statement block.
- `System` is imported automatically. Defaults also import `System.Linq`, `System.Collections`, `System.Collections.Generic`, `System.Text.Json`, and `System.Security.Cryptography`. The worker permits only `SHA256.HashData(byte[])` for cryptography, even if trusted configuration explicitly includes the cryptography namespace. Trusted configuration can replace the optional defaults with `assembly_config_action: "override"` and an explicit `allowed_assemblies` list. Worker restrictions still apply.
- Console access is prohibited. The returned value is converted with `.ToString()`; collections are not automatically serialized. Return readable text with `string.Join`, or explicitly serialize supported data with `JsonSerializer.Serialize`.
- Use `using (...) { ... }` to dispose resources. The current script compiler rejects top-level `using var` declarations.
- Each call runs in a fresh, single-use worker, which may already be prewarmed. Variables do not persist between calls. For either input form, the host saves a unique `.csx` execution copy in the session scratchpad and returns its path.
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
