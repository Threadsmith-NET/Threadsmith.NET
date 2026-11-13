# Scripting execution reference

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Completed [session-scoped scripts](maintenance-scratchpad-scripts.md), existing packaged documentation, and scripting defaults with JSON support.

## 1 Objective
Give agents an accurate, concise execution reference with executable examples.

## 2 Architectural Context
Reuse the installed documentation bundle, tool-description path token, ordinary read policy, and tracked scripting worker.

## 3 Scope
Separate user-facing viewer/configuration guidance from execution rules; test exact documentation examples.

## 4 Non-Scope
New prompts, tools, permission grants, output serialization, or worker lifecycle changes.

## 5 Current State
The existing on-demand reference mixes file APIs with viewer and clipboard instructions.

## 6 Proposed Design
Point the existing reference token and exact-document read exception to `csharp-scripting-execution.md`. Keep user help at `csharp-scripting.md`. Extract the execution reference's C# code fences in real-worker tests.

## 7 Public Contracts
The reference remains on demand. Tool schema and existing execution contracts are unchanged.

## 8 Project/File Changes
Update ScriptFilePolicy, parent/child read-policy tests, scripting tests, the two prompt-reference documents, and relevant help links. The existing operations-document packaging glob includes the new reference.

## 9 Ordered Tasks
Split documentation, switch the reference path, test examples and reads, then run packaging, build, and test validation.

## 10 Testing
Execute computation and JSONL examples verbatim. Assert filtering, record and character bounds, and early termination before malformed trailing input. Verify installed-reference access through the existing read tool for parent and child contexts.

Validated on Windows:

- `dotnet restore src/Threadsmith.sln` — passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` — passed, zero warnings/errors.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-method '*ExecutionReferenceExamplesRunInWorker'` — both examples passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` — 6,223 passed, 88 skipped, zero failed.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Tools/ScriptFilePolicy.cs tests/Threadsmith.ModelTooling.Tests/ScratchpadScriptTests.cs tests/Threadsmith.Architecture.Tests/SecurityPolicyConfigurationTests.cs` — passed.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Tools/ScriptFilePolicy.cs tests/Threadsmith.ModelTooling.Tests/ScratchpadScriptTests.cs tests/Threadsmith.Architecture.Tests/SecurityPolicyConfigurationTests.cs` — passed.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` — passed; includes the new execution reference.
- `git diff --check` — passed. Linux/macOS checks were not run locally.

## 11 Security/Permissions
Only the exact installed execution reference receives the existing special read treatment; file and script policy remains host-owned.

## 12 Observability
Existing tool calls, script source receipts, and execution results remain the observable path.

## 13 Migration/Compatibility
Installed builds ship both documents. Existing user-help links continue to resolve. No configuration migration is required.

## 14 Acceptance Criteria
The advertised reference resolves in packaged help, contains accurate execution rules without viewer instructions, and all its C# examples pass real-worker tests.

## 15 Risks
Documentation examples can drift; tests consume their exact text. Installation packaging or path drift is covered by the ordinary-read integration test and documentation preflight.

## 16 Documentation
Update user help, security/read-permission links, and both prompt token catalogs. Preserve the completed historical script contract.

## 17 Open Decisions
None.
