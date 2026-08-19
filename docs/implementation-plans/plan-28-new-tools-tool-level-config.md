# Plan 28 — New Built-in Tools and Tool-Level Configuration

**Milestone:** 7.2 (Quality of Life Enhancements)
**Prerequisites:** plan-27
**Depends on by:** none
**Status:** Implementation complete; maintained manual verification remains.

## 1 Objective

Add a default-enabled `datetime` tool, a default-disabled bounded `csharp_script` tool, and arbitrary repository-scoped scalar configuration beneath `tools:config:<tool-id>`.

## 2 Architectural Context

The original draft placed configuration and Roslyn scripting in `Threadsmith.Core`. That conflicts with strategy §8.1: Core is dependency-free and Roslyn-free. The implemented design keeps host-owned configuration/tool contracts in `Threadsmith.Tools` and isolates `Microsoft.CodeAnalysis.CSharp.Scripting` in a dedicated executable worker.

In-process scripting also cannot satisfy the required kill-on-timeout behavior because arbitrary script loops need not observe a cancellation token. Each invocation therefore starts a fresh tracked worker through `IProcessManager`; timeout or cancellation kills its process tree. The worker is an isolation and lifecycle boundary, not an operating-system security sandbox.

## 3 Scope

- `IToolConfig` / `ToolConfig` layered scalar configuration.
- Built-in `datetime` tool.
- Built-in `csharp_script` tool and host-owned request/result contracts.
- Fresh-process Roslyn execution, reference/import restriction, prohibited-capability checks, UTF-8 output bounding, timeout, cancellation, and no state reuse.
- Default-disabled metadata and persistence that does not accidentally create a repository allowlist.
- Composition, central package management, configuration example, automated tests, architecture checks, README, manual regression coverage, and DOX.

## 4 Non-Scope

- A hardened operating-system sandbox or untrusted-code security boundary.
- NuGet restore, repository assembly references, globals, stateful submissions, file/network/process/native access, or script-authored host objects.
- A separate interactive command; availability remains controlled by `/tools`.

## 5 Current State

Plan 27 supplies the shared availability-aware registry and repository persistence. Plan 28 adds `ToolDefinition.EnabledByDefault`; ordinary tools retain `true`, while `csharp_script` uses `false`. A private `tools:defaultEnabledOverrides` persistence entry records explicit enablement only when no `tools:enabled` allowlist exists, preserving Plan 27 semantics across restart.

## 6 Proposed Design

### Date/time

`DateTimeTool` uses `TimeProvider`, returns round-trip UTC/local values, `TimeZoneInfo.Local.Id`, and the effective offset. It requires only `UntrustedInspection`, has no approval requirement, and is enabled by default.

### Tool configuration

`ToolConfig` reads `tools:config:<tool-id>:<key>`, converts scalar values using invariant conversion, returns caller defaults for absent values, and exposes all scalar children for one tool. Invalid conversion fails loudly without logging the value.

### C# scripting

`CSharpScriptTool`:

- stable id `csharp_script`, display name `C# Script`;
- `CodeExecution` / `ExecutesCode`, non-idempotent;
- requires `FullyTrustedAutomation`;
- disabled by default and still subject to invocation-policy allow/deny;
- accepts `expression` or `statement`, bounded to 65,536 characters.

`CSharpScriptEngine` reads:

- `timeout_ms` — default 5000, accepted 100..30000;
- `max_output_bytes` — default 65536, accepted 256..1048576;
- `allowed_assemblies` — default `System.Linq,System.Collections,System.Collections.Generic`, maximum 32 entries.

The engine sends one JSON request over standard input to `Threadsmith.Scripting.Worker` through the tracked process manager. The worker constructs fresh `ScriptOptions`, compiles/evaluates one submission, formats the result, and emits one host-deserializable JSON result. The host never durably records raw script arguments.

Defense-in-depth rejection covers `#load`/`#r`, file and directory APIs, network APIs, child processes, environment access, reflection, native interop, dynamic code, unsafe/stack allocation, and fully qualified `System.*` namespaces outside the allowlist. Output is bounded by UTF-8 bytes. Timeout and cancellation terminate the worker tree.

## 7 Public Contracts

- `IToolConfig`, `ToolConfig`
- `DateTimeInput`, `DateTimeOutput`, `DateTimeTool`
- `ScriptKind`
- `CSharpScriptInput`, `CSharpScriptOutput`
- `ICSharpScriptEngine`, `CSharpScriptEngine`, `CSharpScriptTool`
- `ToolDefinition.EnabledByDefault`
- `ProcessExecutionRequest.StandardInput`
- `ToolCategory.SystemInformation`, `ToolCategory.CodeExecution`

All public contracts are host-owned and contain no Roslyn types.

## 8 Project/File Changes

- `Directory.Packages.props` — centrally pins `Microsoft.CodeAnalysis.CSharp.Scripting` at the existing Roslyn version.
- `src/Threadsmith.Scripting.Worker/` — isolated Roslyn executable and local DOX contract.
- `src/Threadsmith.Tools/` — config, tools, engine adapter, metadata, and process standard input.
- `src/Threadsmith.App/` — worker deployment and composition.
- `tests/Threadsmith.Milestone3.Tests/` — externally observable tool/config/worker tests.
- `tests/Threadsmith.Architecture.Tests/` — project graph and config schema assertions.
- `.threadsmith/config.example`, `README.md`, milestone/manual plan, and owning AGENTS files.

## 9 Ordered Tasks

1. Add host-owned tool configuration.
2. Add default-enabled metadata with restart-safe explicit overrides.
3. Add `datetime` contracts/tool.
4. Add worker project and central Roslyn scripting dependency.
5. Add tracked standard-input process support.
6. Add scripting engine/tool, bounds, restrictions, and composition.
7. Add focused automated tests and architecture/config assertions.
8. Update user documentation, manual regression coverage, milestone status, and DOX.

All implementation tasks are complete.

## 10 Testing

Automated coverage verifies:

- UTC/local/timezone/offset consistency and registration metadata;
- typed/default/all-values configuration behavior;
- expression and statement execution;
- output truncation;
- prohibited capability and non-allowlisted namespace rejection;
- process-tree timeout cleanup;
- disabled-by-default advertisement/resolution and persisted enablement;
- complete solution build and dependency/config architecture gates.

Maintained MTP-030G owns real host/model `/tools` and scripting verification.

## 11 Security/Permissions

- Scripting is disabled by default and requires `FullyTrustedAutomation`.
- Availability cannot widen invocation policy or repository trust.
- The worker receives a bounded JSON request via standard input; raw code is not placed on the command line or logged.
- The process manager strips unapproved environment variables, tracks the child, bounds streams, sanitizes output, and kills the process tree.
- Roslyn reference restriction and syntax/name rejection are defense in depth, not a claim that managed code is an OS sandbox. Only trusted repositories should enable this tool.

## 12 Observability

Both tools use the existing invocation pipeline and therefore emit standard tool activity, durable completion, latency, failure classification, budget, and cancellation events. The nested worker is visible through existing active-process tracking without exposing script text.

## 13 Migration/Compatibility

Existing tool definitions remain enabled by default. Existing `tools:enabled`/`tools:disabled` and invocation-policy semantics are unchanged. Repositories that explicitly enable a default-disabled tool without an allowlist receive a host-owned `tools:defaultEnabledOverrides` entry; unrelated JSON is preserved atomically.

## 14 Acceptance Criteria

- `datetime` is enabled and returns correct current host clock metadata.
- `csharp_script` is registered, disabled by default, restart-persistent when enabled, and unavailable when disabled.
- Expressions and statement sequences execute in fresh workers.
- Invalid compilation returns bounded diagnostics.
- Timeout/cancellation kills the worker tree.
- Output respects configured UTF-8 byte limits.
- File/network/process/native/reflection/dynamic/directive and non-allowlisted namespace attempts are rejected.
- Tool config binds arbitrary scalar values with documented defaults and bounds.
- Core remains dependency-free and no Roslyn type crosses a host boundary.
- Build, focused tests, architecture tests, documentation, manual plan, and DOX are current.

## 15 Risks

- Managed-code restrictions are not a formal security sandbox. Mitigation: default-off, highest repository trust, fresh process, narrow references/imports, prohibited-capability checks, environment stripping, bounded output, and process-tree kill.
- Worker startup adds latency. Mitigation: correctness and reliable termination take precedence; workers are never retained between calls.
- Runtime/package resolution can vary by deployment. Mitigation: the App references and ships the worker plus centrally pinned dependencies, and startup fails closed if the worker is absent.

## 16 Documentation

Updated `.threadsmith/config.example`, `README.md`, `milestones.md`, `manual-test-plan.md`, root/source/tool/test DOX, and this plan.

## 17 Open Decisions

A future hardened untrusted-code feature would require an explicit operating-system sandbox/container design. It must not silently weaken or replace this trusted, default-disabled worker contract.
