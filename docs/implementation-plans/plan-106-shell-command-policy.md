# Plan 106 — Configurable shell command policy

**Status:** Planned
**Delivery track:** Maintenance
**Prerequisites:** Existing shared tool policy and `run_process` execution pipeline; configuration layering; governed Web Search/Web Fetch authorization; deployed prompt assets; main/subagent tool invocation parity.

## 1 Objective

Allow maintainers to control commands submitted through `run_process`, with allow-list and deny-list modes and an explicit policy directing web research through `web_search` and `web_fetch`. Rejected commands must not start a process and must return actionable model correction. This is command-routing enforcement, not an operating-system sandbox.

## 2 Architectural Context

`tools.allowedExecutables` is shared by native tools and checks the executable each tool declares. `run_process` declares its configured outer shell, so allowing PowerShell or Bash currently permits its nested commands. Preserve that existing check and add command analysis at the shared invocation-policy boundary. Follow `AGENTS.md`, portable C# guardrails, and planning governance; do not add provider-specific or frontend-specific enforcement.

## 3 Scope

- Typed, validated configuration under `tools.runProcess.commandPolicy` using the established hierarchy.
- Shell-aware analysis of actual command invocations, pipelines, nested expressions, and supported literal shell wrappers.
- Common policy evaluation for main agents, subagents, interactive, and headless execution of `run_process`.
- Governed-web correction, effective-policy inspection, docs, and Windows/Linux/macOS verification.

## 4 Non-Scope

- OS network isolation, firewall configuration, arbitrary script interpretation, or proof that child processes cannot access the network.
- Restrictions on unrelated native build, restore, Git, MCP, or extension operations through this setting.
- Automatically enabling web tools, granting consent, changing repository trust, or executing a blocked request via another tool.
- Implementing this feature as part of creating this plan.

## 5 Current State

`RunProcessTool` accepts `command` and an optional timeout, selects PowerShell on Windows and Bash elsewhere by default, and also supports pwsh, sh, zsh, and cmd. `DefaultPolicyEngine` checks its declared outer executable against `AllowedExecutables`. It does not inspect shell commands. The tool advertises no network-host claims; shell HTTP operations therefore bypass the host checks applied by governed web tools. Web-tool enablement and consent remain repository-specific, so an unavailable web tool must be reported honestly in correction text.

## 6 Proposed Design

### Configuration and precedence

```json
{
  "tools": {
    "runProcess": {
      "commandPolicy": {
        "mode": "denyList",
        "allowedCommands": [],
        "deniedCommands": [],
        "webAccess": "governedToolsOnly"
      }
    }
  }
}
```

`mode` accepts `denyList` and `allowList`; `webAccess` accepts `governedToolsOnly` and `allow`. The example is the proposed shipped default. In deny-list mode, commands are permitted unless explicitly denied or rejected by the web rule. In allow-list mode every analyzed command must be allowed; an empty allow-list permits none. Explicit denials and the governed-web rule take precedence over allowed entries. Setting `webAccess` to `allow` removes only the special web restriction, not other denials.

Use existing machine, user, repository, session, CLI, and environment precedence and trust rules; do not invent another settings store. Each list replaces lower-precedence lists in full, including an explicit empty list. Validate enum values and entry syntax at configuration load with the key and actionable reason. Policy scope and origin must be inspectable. This operator-configured hierarchy is not a repository-independent security boundary: a repository override is configuration input under the existing trust model.

Entries are exact command identities, not substring expressions, regex, or arbitrary shell code. Support bare names and fully qualified executable paths with documented matching rules. Normalize Windows executable suffixes/case and PowerShell command case without imposing Windows semantics on Unix executable paths. Preserve path-qualified identities for evaluation; a path-specific denial must not disappear through basename normalization. Define and test treatment of module-qualified cmdlets, known aliases, and absolute executable paths.

### Analyze once, enforce centrally

Introduce one cohesive command-policy component in `Threadsmith.Tools`, composed through the existing host configuration. Keep shell parser implementations private behind host-owned analysis/decision records. Analyze the unchanged command string before process launch; neither execute the command to discover its meaning nor silently rewrite it. Cache only immutable configuration/alias metadata, not authority decisions across policy changes.

Use a maintained parser where practical, following a small dependency/packaging evaluation. Do not build a permissive regex parser and call it shell validation. Establish an explicit syntax support matrix for PowerShell/pwsh, Bash/sh, zsh, and cmd; unsupported syntax must produce a clear diagnostic according to mode. Shared evaluators consume normalized invocations, while shell adapters own syntax and lexical scope. Do not add a new project solely for this component.

Inspect every pipeline stage and nested command expression. Handle quoted executable names, command substitutions, command separators, invocation operators, module qualification, and known literal shell/interpreter wrappers. Text inside a comment or literal argument such as `echo "curl"` is not a command. Dynamically computed targets, eval, aliases/functions defined at runtime, script files, interpreter programs, and nested shells that cannot be analyzed are explicitly classified as unresolved.

Allow-list mode rejects unresolved executable behavior rather than treating an allowed interpreter as blanket authorization. Deny-list mode checks recognized invocations and may allow unresolved behavior; report that limitation in effective-policy details. No mode promises restrictions on the internals of allowed binaries or build scripts. Do not make an unsupported parser silently behave as unrestricted enforcement.

### Governed web routing

Recognize dedicated HTTP retrieval invocations such as PowerShell `Invoke-WebRequest`/`Invoke-RestMethod` and their known aliases, and curl/wget equivalents on supported platforms. Separate this named capability classification from operator command lists so the operator need not maintain an OS-specific list for the basic policy. Document exactly which command forms are covered. Do not infer intent from arbitrary URLs in arguments or block all networking: package restore and Git operations remain subject to their own policies.

When a recognized retrieval is blocked, use the existing failed-tool/correction mechanism with a stable reason code and deployed prompt wording. Direct the model to `web_search` for discovery or `web_fetch` for a known URL. If the required tools are disabled, consent-blocked, or unavailable to that agent, explain that the user must enable/authorize them; do not claim a call can succeed or provide a shell workaround. Inspect existing eligibility state without mutating it. Existing continuation budgets apply; no new model round is added for successful commands.

## 7 Public Contracts

- Immutable command-policy options and closed mode/web-access values, with validation at the configuration boundary.
- Host-owned analysis/decision data identifying allowed, denied-command, not-allowed, governed-web-required, or unsupported-analysis outcomes.
- No parser-library types in Core, tool results, persisted state, or frontend contracts.
- Keep the existing `run_process` model schema (`command`, `timeoutSeconds`); model arguments cannot override policy.

## 8 Project/File Changes

- `Threadsmith.App/HostFoundation.cs` and existing configuration composition: resolve and inject effective options without duplicating precedence logic.
- `Threadsmith.Tools/BuiltInTools.cs`, `ToolPolicy.cs`, and focused new command-policy/parser files: analysis and central admission.
- Existing tool-state/inspection projections: expose effective mode, lists, web behavior, and limitations.
- Deployed `Tool-run_process-Description.md` and correction assets, with their complete token catalog.
- Existing tool-policy, configuration, provider-neutral conversation, and process tests; no provider forks.

## 9 Ordered Tasks

1. Trace actual preparation/execution paths and configuration rebind semantics; identify the single admission point shared by all callers.
2. Evaluate parser support and package footprint for each supported shell; record the accepted grammar and unsupported constructs before implementation.
3. Add validated options, explicit list replacement, configuration origin inspection, and repository rebind coverage.
4. Implement shell adapters and normalized command identities; exercise the syntax matrix before connecting enforcement.
5. Add the shared evaluator, web classification, deployed correction assets, and pre-launch denial.
6. Connect existing main/subagent/headless paths and verify current effective policy is used at invocation.
7. Update maintained documentation and sample configuration; execute the cross-platform test matrix.
8. Perform independent adversarial review, address valid findings, and re-review until clean before release.

## 10 Testing

Test default configuration, hierarchy overrides, empty-list replacement, invalid values, repository changes, denial precedence, and path/case/alias identities. Verify the process manager is never invoked for rejected commands. Include the observed `(Invoke-WebRequest -Uri "https://huggingface.co/.../README.md").Content` and `Invoke-RestMethod ... | ConvertTo-Json` cases.

Cover pipelines with a forbidden stage, nested expressions, literal nested shells, dynamic targets, comments, quoted strings, benign arguments containing command names, module-qualified cmdlets, Unix command substitutions, executable suffixes, and full paths. Confirm ordinary Git and package restore calls are unaffected. Confirm unavailable web tools yield truthful correction and cannot gain enablement or consent. Use synthetic URLs and fake process execution for policy tests; live network is unnecessary to verify rejection.

Run supported-shell integration cases on Windows, Linux, and macOS. Unsupported shells/constructs must have explicit expected outcomes, not skipped enforcement. Verify cancellation, concurrent main/subagent calls, policy changes, and unchanged tool activity formatting. Measure local analysis overhead on normal and large valid commands without adding provider latency.

## 11 Security/Permissions

Preserve trust, executable, approval, timeout, output, and governed-web authority checks. Model text cannot change policy. Parser evaluation must not execute code or resolve aliases by invoking an untrusted shell. Audit the distinction between direct recognized web commands and indirect networking in allowed code. A future OS sandbox is separate work; do not describe this feature as containing hostile programs.

## 12 Observability

Tool details show active mode, configured identities, web-routing setting, configuration scope, and parser coverage. Denied tool activities use existing outcome rendering and sanitized reasons. Do not retain raw credentials, entire commands, or URL queries in added diagnostics. Successful calls retain existing timers and output format.

## 13 Migration/Compatibility

`tools.allowedExecutables` keeps its existing outer-process meaning. The new default `governedToolsOnly` is a deliberate behavior change for recognized shell HTTP retrieval; release notes must state it and document `webAccess: "allow"`. Existing arbitrary shell usage remains available in deny-list mode subject to explicit rules. No legacy parallel configuration system or automatic user-config rewrite is needed.

## 14 Acceptance Criteria

- The configured policy applies consistently before every `run_process` launch through all supported agent/frontends.
- Both allow-list and deny-list modes honor explicit denials, hierarchy, and empty-list semantics.
- Recognized web retrieval is blocked by default with useful, truthful governed-tool correction.
- Literal text is not misclassified as invocation; supported nested commands cannot bypass analysis through ordinary syntax.
- Unresolved syntax follows the documented mode behavior; accepted options are never silently ignored.
- Native build/restore/Git paths and repository capacity are unchanged; no repository admission limits are introduced.
- Supported platform tests pass; docs accurately state coverage and lack of OS isolation.

## 15 Risks

Shell grammar and dynamic execution are the main complexity. Contain this in shell adapters and a common evaluator rather than proliferating call-site exceptions. Parser dependencies may add deployment cost or have incomplete zsh/cmd coverage; choose explicitly and fail clearly where strict analysis is unavailable. Dedicated HTTP commands can also serve non-research uses; operators can opt out of the web rule. Deny-list mode cannot guarantee prevention of indirect HTTP access.

## 16 Documentation

At implementation, update the user guide, tool policy documentation, configuration reference and sample, Web Search/Web Fetch operational guidance, prompt reference/catalog, and release notes. Add maintained acceptance/manual cases for changed behavior without reopening completed milestones. This plan owns proposed scope; shipped docs must not imply the feature already exists.

## 17 Open Decisions

Select parser packages/adapters and the precise supported grammar during task 2 after inspecting dependency footprint and cross-platform support. No policy-default or scope decision remains open: both modes, denial precedence, the existing hierarchy, governed-web default, and the explicit non-sandbox limitation are required.
