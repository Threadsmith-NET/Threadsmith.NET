# AGENTS.md — Threadsmith.Scripting.Worker

## Purpose

Own the disposable Roslyn process used by the optional `csharp_script` built-in so arbitrary loops can be terminated without retaining script state in the host.

## Ownership

- `Program.cs` — bounded standard-input protocol, reference/import restrictions, normalized syntax and semantic prohibited-capability checks, fresh script evaluation, UTF-8 output bounding, and JSON response.
- `Threadsmith.Scripting.Worker.csproj` — isolated Roslyn scripting dependency and executable deployment boundary; it declares the complete supported release RID matrix so split restore/publish workflows resolve the worker target, while `eng/release/` publishes and requires its apphost explicitly for the application's RID.

## Local Contracts

- Read exactly one bounded JSON request from standard input and write exactly one JSON result to standard output.
- Never accept script text on command-line arguments, write files, open network connections, launch children, log script text, or retain state between invocations.
- Reject directives, file/network/process/environment/reflection/native/dynamic/unsafe APIs, and `System.*` namespaces outside the configured allowlist by inspecting parsed syntax and resolved symbols before evaluation.
- This process is a lifecycle/isolation boundary, not an operating-system security sandbox. The host must keep the tool disabled by default, require trusted execution, and kill this process tree on timeout/cancellation.
- Reference no `Threadsmith.*` project; the JSON protocol uses worker-local DTOs and the host maps to host-owned contracts.

## Work Guidance

- Keep Roslyn types private to this executable.
- Preserve deterministic bounded output and fresh `ScriptOptions` per request.
- Do not add NuGet restore, repository assembly loading, globals, or stateful continuations.

## Verification

- `dotnet build src/Threadsmith.sln --no-restore`
- `tests\Threadsmith.ModelTooling.Tests\bin\Debug\net10.0\Threadsmith.ModelTooling.Tests.exe --filter-method "*CSharpScript*"`

## Child DOX Index

No child AGENTS.md files yet.
