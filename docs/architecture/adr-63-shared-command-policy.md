# ADR-63: Shared command policy with immutable shell binding

- **Status:** Accepted for implementation; cross-platform evidence remains required before release.

## Context

Allow-listing a shell executable does not establish which commands it will run. Command decisions also need deterministic rule resolution, precise approval scopes, source ownership, and a reusable evaluation boundary. This does not create an OS sandbox.

## Decision

`Threadsmith.Policies` is a leaf library: it references no `Threadsmith.*` project, process launcher, frontend, provider, or compiler API. It owns typed facts and matchers, strict configuration, compilation, outcomes, override graphs, grant bookkeeping, and the configuration-transformation service. Core can carry its immutable host DTOs, but command snapshots are excluded from ordinary session serialization.

Tools owns bounded literal shell preparation. It resolves external executables from the frozen child environment, builds one immutable launch, and evaluates every action before any launch. Absolute external targets are quoted for the selected dialect. A simple literal name absent from external lookup is a `shell-command` action with no resolved file target. It uses the same command-name rules, approvals and argument-deny expressions. This permits cmdlets, builtins and aliases without command-specific parsing or discovery probes. It grants the literal name, not a fixed implementation; aliases are not canonicalized and shell lookup can select modules, functions or cwd files. Native resolution takes precedence. Explicit/relative paths do not become shell-name fallbacks. Shell-command actions are admitted only as a single action: compounds containing an unresolved shell name are rejected because name permission cannot establish its effects on later directory facts. External-only compounds remain supported. Unsupported syntax fails closed. ProcessManager remains the only tracked process path; request provenance, cancellation, limits and process-tree cleanup do not change.

Command policy extends DefaultPolicyEngine and ToolInvocationPipeline. Existing trust, availability, child restrictions, protected paths, hooks, budgets and mandatory review still apply. Approval is projected by the existing InteractionCoordinator modal owner. Generic tool review intersects command choices into one prompt; it cannot be bypassed by a remembered command grant.

Executable authorization and optional argument restrictions are policy data, never command-name branches in production C#. Shipped defaults use the same strict JSON contract as machine/user rules. A rule's optional `denyArgumentsMatching` array contains bounded non-backtracking regex searches. Any expression matching an applicable rule denies the invocation, before approvals or outcome overrides. Expressions search parsed literal argument values joined with single spaces, excluding the command name, without further normalization. Matching is case-sensitive by default; inline `(?i)` is supported. Without expressions, arguments have no additional restriction. There are no command-specific grammars, option-consumption rules or argument-coverage requirements. Users can replace ordinary shipped rules without modifying application code.

Only machine and user sources define this policy. Machine enforced constraints do not grant ordinary permission and cannot be overridden by user rules or approvals. Unmatched actions and unresolved conflicts deny without prompting. Overrides are explicit, validated across profiles, and independent of array order.

All host-managed list changes use PolicyConfigurationService and the existing RepositorySettingsCoordinator publication gate. RepositoryScopedBindingCoordinator validates effective policy before publication, invalidates old authority, and activates only a validated focused refresh. A failed post-save activation is distinct from a failed save; authority remains unavailable. Manual reload uses the same validation and activation path without rewriting the document.

Live approvals belong to their session/authority epoch, not checkpoints. Once is consumed at final admission; queued remembered admissions recheck revocation. Persistent approvals store exact context and rule fingerprints in the user `alwaysAllow` collection. More-specific profiles replace inherited grants with the same ID. Fingerprints canonicalize object-property order.

The existing primary `ToolOperationScope` owns correlated Always publication continuation for its conversation and descendants. Only completed, correlated publication chains can advance future round captures. Each capture retains its own assignment ceilings; queued invocations remain immutable. Unrelated invalidations and session resets cannot advance those captures. Explicit manual reload reuses the existing idle-only full repository rebind and can recover after configuration repair. It does not retain an executable fallback or a second authority baseline. Always saves keep the focused active-operation path.

Detailed policy data is user-only: local management and approval surfaces may show rules, sources, targets and grant IDs. Model prompts, ordinary events, tool results and retrieved diagnostics receive only generic success or denial, never policy catalogs or suggested alternatives.

## Limits

The parser admits only a bounded static subset. WSL wrappers are not native Windows POSIX shell bindings. An admitted binary may use its ambient account authority, network, packages, hooks, helpers and descendant processes. Files can still change after authorization; pinning a path does not remove filesystem replacement races. Real-shell evidence must be reported for each OS/dialect pair, not inferred from another pair.

See the [user policy reference](../operations/command-policy.md) and [central pipeline decision](adr-11-central-tool-policy-pipeline.md).
