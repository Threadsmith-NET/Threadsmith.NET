# Plan 40 — Lifecycle Hooks and Policy Automation

**Milestone:** M13 — Lifecycle Hooks and Policy Automation

**Prerequisites:** plans 02, 05, 07–10, 12–20, 27, 30–31, and 37–39

**Depends on by:** future organization policy distribution, CI policy packs, and governed external automation

**Status:** Implementation and focused automated coverage complete; maintained real executable/HTTP/MCP/extension and interruption closeout remains.

## 1 Objective

Add host-owned lifecycle hooks that can notify or consult typed executable, HTTP, MCP, and extension handlers at stable repository, model, tool, planning, mutation, validation, correction, run, extension, and MCP boundaries.

Hooks are advisory by default. A hook may block host progress only when a trusted non-repository managed policy explicitly grants blocking authority for a specific hook point and handler identity. Every invocation is cancellable, bounded, secret-scoped, audited, and governed independently of the model, repository content, extension declaration, or handler response.

## 2 Architectural Context

Threadsmith already has immutable domain events, a command/state-machine boundary, centralized tool policy, transactional mutation, build/test validation, extension invocation leases, MCP connection lifecycle, persistence, redaction, and resumable execution. These are the correct sources for lifecycle automation, but the existing event stream is observation infrastructure rather than an authorization or callback API.

M13 adds a distinct hook coordinator over explicit host boundaries. It does not let subscribers intercept arbitrary events, replace core services, mutate event payloads, or become an alternate execution engine. The host constructs a minimal typed envelope, evaluates policy, invokes eligible handlers through transport-specific adapters, validates bounded typed results, records an audit decision, and alone decides the legal next transition.

The user-approved M13 feature is an explicit post-strategy addition. The strategy remains authoritative for all existing host-control, policy, event, cancellation, adapter, persistence, and security constraints.

## 3 Scope

Stable hook points for:

- repository opened;
- before and after model request;
- before and after tool invocation;
- plan proposed and plan approved;
- mutation staged and mutation applied;
- before and after validation;
- correction started;
- run completed and run failed;
- extension connected/activated;
- MCP connected.

Also in scope:

- Versioned host-owned hook-point, envelope, context, outcome, advice, and denial contracts.
- Executable-process, HTTP, MCP, and extension handler adapters.
- Explicit installation/configuration trust, repository approval, enablement, handler identity, and immutable configuration fingerprints.
- Advisory versus managed-blocking authority.
- Declared fail-open/fail-closed behavior, restricted by hook point and managed policy.
- Per-handler timeout, input/output, concurrency, retry, and aggregate run budgets.
- Least-privilege secret references and per-invocation secret scope.
- Deterministic ordering, cancellation, deduplication, recursion prevention, and durable audit events.
- TUI/headless list, inspect, approve, enable/disable, test, and audit surfaces.
- Persistence/restoration, telemetry/redaction, diagnostics, configuration, automated tests, and maintained manual cases.

## 4 Non-Scope

- Repository hooks enabled merely because files or configuration exist in the repository.
- Arbitrary event-bus subscriptions with blocking or mutation authority.
- Handler modification of model prompts, tool arguments/results, plans, mutation bytes, validation results, extension registrations, MCP tools, domain events, or host policy.
- Hook-returned shell commands, patches, approvals, credentials, capabilities, workflow steps, or executable code.
- Hook handlers granting trust, widening secret scope, enabling tools, approving plans/mutations, skipping validation, or changing retry/budget limits.
- In-process loading of repository-provided executable code.
- Treating processes, HTTP endpoints, MCP servers, extensions, signatures, or repositories as security sandboxes.
- Unbounded retries, parallel fan-out, response bodies, output capture, environment inheritance, or callback recursion.
- Replacing Plan 37 execution orchestration, Plan 38 delegation, Plan 39 workflows, the central tool pipeline, or the domain event stream.

## 5 Hook Point Contract

Define a closed, versioned `HookPoint` catalog. Each point declares whether it is observational, pre-action, or terminal; eligible outcome kinds; maximum authority; legal failure behavior; required correlation IDs; and the host boundary that emits it.

Initial points:

| Hook point | Timing | Maximum authority |
|---|---|---|
| `RepositoryOpened` | after trusted repository lifecycle result | advisory |
| `BeforeModelRequest` | after context/policy assembly, before provider I/O | managed blocking |
| `AfterModelRequest` | after normalized provider result/failure | advisory |
| `BeforeToolInvocation` | after schema/availability checks, before execution | managed blocking |
| `AfterToolInvocation` | after normalized tool result/failure | advisory |
| `PlanProposed` | after schema validation, before approval request | managed blocking |
| `PlanApproved` | after host/user approval is durable | advisory |
| `MutationStaged` | after exact diff artifact is durable, before application authorization | managed blocking |
| `MutationApplied` | after transaction reconciliation is durable | advisory |
| `BeforeValidation` | before a defined build/test validation scope starts | managed blocking |
| `AfterValidation` | after authoritative normalized validation evidence | advisory |
| `CorrectionStarted` | after host enters a bounded correction attempt | advisory |
| `RunCompleted` | after authoritative terminal outcome is durable | advisory |
| `RunFailed` | after authoritative terminal failure is durable | advisory |
| `ExtensionConnected` | after activation/registration succeeds | advisory |
| `McpConnected` | after connection and imported-capability publication succeeds | advisory |

“Before” does not imply interception authority. Without an effective managed blocking grant, a denial-like response is recorded as advice and cannot stop the action. After/terminal points can never retroactively invalidate or roll back a completed host action.

Each invocation receives a host-owned `HookInvocationEnvelope` containing schema version, hook point, invocation ID, handler identity/version/configuration digest, session/run/repository correlation, operation ID, timestamp, attempt/generation, sensitivity classification, bounded point-specific payload, and artifact references where policy permits. Payloads contain normalized DTOs and summaries, not live provider, Roslyn, process, terminal, MCP SDK, persistence, or extension implementation objects.

## 6 Handler Results and Authority

Handlers return one closed result union:

- `Acknowledge` — handled with no finding;
- `Advice` — bounded typed findings, labels, and artifact references;
- `Deny` — stable denial code and bounded explanation;
- `Failure` — normalized handler failure classification.

Unknown schema versions, outcome kinds, excessive findings, malformed fields, undeclared artifacts, or conflicting identities are failures. Free text is untrusted display/evidence content and never parsed as commands or policy.

An advisory handler’s `Deny` is converted to advice. A blocking denial is effective only when all are true:

1. the hook point permits managed blocking;
2. a trusted machine/user/organization policy outside repository control names the immutable handler and hook point;
3. the policy grants a bounded denial-code set and declares fail behavior;
4. current repository/session policy has not disabled that optional policy pack where disabling is permitted;
5. the handler invocation and result pass identity, freshness, schema, budget, and integrity checks.

The host records both the raw normalized outcome and the effective decision. A handler can veto one pending action; it cannot approve an action or select the next transition. User approval cannot override a mandatory organization denial unless that same managed policy defines an explicit authorized override path.

## 7 Configuration, Trust, and Repository Approval

Configuration is layered using existing trusted configuration infrastructure. A handler declaration includes stable ID, type, hook points, adapter-specific target, immutable version/configuration digest, enablement, advisory/blocking request, timeout/output/concurrency/retry limits, secret references, sensitivity eligibility, and declared fail behavior.

Trust and enablement are separate:

- **Organization/machine/user handlers:** may be trusted and enabled only by the owning non-repository scope and are still constrained by host policy.
- **Repository handlers:** declarations are untrusted requests. They remain disabled until an explicit user action approves the exact repository identity plus normalized handler configuration digest, hook-point set, target identity, limits, secret-reference names, and authority (advisory only by default).
- Repository approval is stored outside repository control, is revocable, and becomes stale when repository identity or approved configuration changes.
- Repository content can never grant managed blocking authority, fail-closed behavior, signer trust, secret values, unrestricted environment access, or endpoint/process exceptions.

Every startup and turn boundary re-resolves current declarations against trust, repository approval, revocation, endpoint/process/extension/MCP state, and limits. Missing, changed, ambiguous, or revoked trust fails the handler closed as unavailable but does not block host work unless an independently trusted managed fail-closed policy requires it.

## 8 Handler Adapters

### 8.1 Executable handlers

Run a configured executable through the existing tracked process manager using argument arrays, a minimal curated environment, confined working directory, redirected bounded standard input/output/error, process-tree cancellation, timeout, and kill backstop. The request and response use versioned JSON over standard streams. No shell interpolation occurs. Repository-relative executable targets require exact user approval and are advisory-only; managed blocking executables must come from a trusted non-repository installation policy.

### 8.2 HTTP handlers

Use a host-owned HTTP adapter with HTTPS by default, endpoint-host policy, bounded redirects/response bytes, connect/request timeout, cancellation, retry classification, and secret-reference headers injected only after target validation. Send one versioned JSON request and accept one bounded JSON response. Repository handlers cannot supply inline credentials, arbitrary authorization headers, proxy overrides, or endpoint-policy exemptions.

### 8.3 MCP handlers

Invoke a configured, already-connected MCP capability through the existing MCP adapter and central tool/policy pipeline. Bind to immutable profile/server/tool identity and schema snapshot. Hook invocation does not auto-connect, broaden imported tools, bypass MCP secret scope, or recursively trigger tool hooks. MCP handlers are advisory by default; managed blocking requires trusted external policy and a stable approved capability identity.

### 8.4 Extension handlers

Resolve a dedicated lifecycle-hook capability through the extension capability registry, acquire an invocation lease, pass only host-owned DTOs, enforce extension budgets/timeouts, and release the lease on every outcome. Extension code remains trusted in-process code but is not automatically policy-authoritative. Extension activation cannot self-register blocking authority; trusted external policy must grant it to an immutable extension/generation-compatible handler identity.

## 9 Ordering, Concurrency, Cancellation, and Recursion

Resolve one immutable handler snapshot per host boundary. Order handlers deterministically by managed policy priority, scope, handler ID, and version; never by filesystem or registration order. Default execution is sequential for blocking-capable pre-hooks. Advisory handlers may use bounded parallelism only when result ordering is restored deterministically and aggregate budgets remain enforceable.

Cancellation is linked to the owning operation. Cancellation stops waiting, propagates through the adapter, records an indeterminate/cancelled hook outcome, and follows the effective fail policy. After an operation reaches a durable terminal boundary, late results are discarded by operation ID and generation fencing.

Hook-originated operations carry a suppression token and call-chain metadata. A handler invocation cannot trigger the same hook recursively. MCP tool execution used as a hook handler suppresses tool before/after hooks for that internal call while retaining a dedicated nested audit record. Depth is fixed and bounded; cycles or duplicate operation/handler pairs fail deterministically.

## 10 Failure Semantics

Every declaration states `FailOpen` or `FailClosed`, but declaration does not grant authority:

- advisory handlers are always effectively fail-open;
- after/terminal hooks are always fail-open for host progress;
- repository handlers are effectively fail-open;
- fail-closed is legal only for a managed blocking grant at an eligible pre-hook point;
- malformed output, timeout, unavailable adapter, trust change, budget exhaustion, and handler failure all use the same effective failure rule;
- cancellation initiated by the user remains cancellation and is not converted into a hook denial.

Fail-open records the failure and continues without fabricating success. Fail-closed prevents the pending action, records a stable policy denial, and moves through the host boundary’s existing legal blocked/failed transition. It does not roll back prior durable actions unless the existing owning subsystem independently requires rollback.

## 11 Secret and Data Scope

Handler configuration may name secret references but never contain values. Before each invocation, the host computes the intersection of handler declaration, adapter target, hook point, repository trust, managed policy, and current sensitivity policy. Only that effective scope is materialized at the latest possible adapter boundary.

Executable secrets use explicit environment-variable or standard-input fields, never inherited ambient environment or command-line arguments. HTTP secrets are attached only to the validated origin and stripped on redirect. MCP secrets remain owned by the selected connection profile. Extension handlers receive opaque host operations or narrowly scoped values only when an extension contract and policy explicitly permit it; secrets do not enter general hook envelopes.

Before-model and tool payloads default to metadata, identities, schemas, hashes, sensitivity labels, and bounded summaries. Raw prompts, file contents, diffs, tool arguments/results, provider content, and validation logs require explicit point-specific data grants. Audit, telemetry, persistence, diagnostics, and handler errors are redacted and never retain secret values.

## 12 Persistence and Audit

Emit immutable domain events for handler discovery/configuration, repository approval/revocation, invocation started/completed/failed/timed out/cancelled, advice recorded, and policy denial applied. Events carry schema version, hook/handler/operation IDs, configuration digest, authority source, effective fail mode, duration, bounded outcome metadata, and artifact references; they never carry secret values or unbounded raw payloads.

Persist repository approvals outside repository control and persist audit/checkpoint records through ordered migrations. Restoration reconciles an interrupted hook invocation by hook point and owning operation: no external handler is blindly replayed. Pre-action resume either re-invokes only when policy explicitly marks the handler idempotent and the operation has no durable outcome, or requires a fresh user/policy decision; post-action hooks may be marked missed and retried only as a separately audited notification with the same operation ID.

Audit views explain which handlers were eligible, skipped, unavailable, invoked, timed out, failed, advised, or blocked; which trust and managed policy supplied authority; which fail mode was effective; which data/secret scopes were granted; and what host transition followed.

## 13 Public Contracts

- `HookPoint`, `HookInvocationId`, `HookHandlerId`, `HookHandlerIdentity`, and `HookConfigurationDigest`.
- Versioned point-specific payload records under a `HookInvocationEnvelope`.
- `HookHandlerResult` closed union with acknowledgement, advice, denial, and failure DTOs.
- `HookAuthority`, `HookFailureMode`, `HookEligibility`, `HookPolicyDecision`, and stable denial/failure codes.
- `HookHandlerDescriptor`, adapter descriptors, limits, secret/data-scope requests, and compatibility results.
- `IHookCoordinator`, `IHookPolicyEvaluator`, and internal adapter boundary contracts.
- Host commands/projections for list, inspect, approve, revoke, enable, disable, test, and audit.

Public cross-subsystem contracts contain no terminal, provider SDK, MCP SDK, extension implementation, process, HTTP implementation, persistence row, or secret-store implementation types.

## 14 Project/File Changes

- `Threadsmith.Core` — hook identities, points, envelopes, results, authority/failure policy, events, commands, and projections.
- `Threadsmith.Execution` — coordinator integration at plan, mutation, correction, and run boundaries; resume/reconciliation rules.
- `Threadsmith.Models` — before/after normalized model-request boundary integration without provider-wire leakage.
- `Threadsmith.Tools` — before/after tool-pipeline integration and recursion suppression.
- `Threadsmith.Workspaces` — staged/applied boundary integration using existing durable operation IDs.
- `Threadsmith.Validation` — before/after aggregate validation boundaries.
- `Threadsmith.Extensions.Abstractions` / `Threadsmith.Extensions.Runtime` — minimal lifecycle-hook capability and leased adapter if the stable-contract threshold is met.
- `Threadsmith.Mcp` — configured MCP hook adapter over existing connections and imported capability contracts.
- `Threadsmith.Persistence` — approvals, configuration fingerprints, audit records, migrations, and tolerant restoration.
- `Threadsmith.Telemetry` — spans, metrics, redaction, and diagnostic projections.
- `Threadsmith.App` — configuration, trusted policy, adapter, and coordinator composition.
- `Threadsmith.Tui` / `Threadsmith.Cli` — shared hook management and audit commands.
- `.threadsmith/config.example` — handler declarations and safe limits; no approval, trust, blocking grant, or secret value.
- Dedicated `Threadsmith.LifecycleHooks.Tests`, fixtures, architecture gates, operations/security docs, acceptance/manual scenarios, and DOX updates.

Any new project-level handler schema or fixture asset must be copied to output when newer.

## 15 Ordered Tasks

1. Record an ADR distinguishing domain events, advisory hooks, and managed blocking policy; define authority, repository approval, failure, and replay decisions.
2. Inventory each source boundary and operation ID; define the closed hook-point catalog and point-specific minimal payloads.
3. Add versioned identities, envelopes, results, descriptors, authority/failure/data/secret scope, limits, events, commands, and projections.
4. Implement layered declaration loading, normalization, immutable configuration digests, deterministic resolution, external repository approval/revocation, and managed-policy grants.
5. Implement the coordinator with eligibility evaluation, deterministic ordering, budgets, cancellation, generation fencing, recursion suppression, output validation, advice aggregation, and effective decisions.
6. Integrate repository, model, tool, plan, mutation, validation, correction, run, extension, and MCP boundaries without duplicating ownership or emitting hooks before authoritative source outcomes.
7. Implement executable-process and HTTP adapters using existing process/network policy, cancellation, secret, and redaction infrastructure.
8. Implement MCP and extension adapters through existing connection/capability registries, leases, policy, and timeout budgets.
9. Add ordered persistence migrations, restoration/reconciliation, idempotency declarations, audit queries, retention, and diagnostic-bundle projections.
10. Add shared interactive/headless list, inspect, approve/revoke, enable/disable, test, and audit surfaces with explicit authority/failure/data-scope disclosures.
11. Add deterministic fake handlers and adversarial executable/HTTP/MCP/extension fixtures covering timeouts, malformed/oversized output, denial, recursion, cancellation, crash/resume, and secrets.
12. Add architecture, policy, redaction, concurrency, event-order, compatibility, and full Scenario M tests.
13. Update configuration examples, event catalog, architecture/security/operations/user documentation, milestones/index/scenarios/manual tests, root status, and affected DOX chains when implementation lands.

## 16 Testing

Automated coverage must verify:

- every hook fires exactly once at its documented authoritative boundary with stable operation correlation and deterministic order;
- after/terminal hooks never block, undo, or rewrite completed work;
- advisory denial and any repository denial are recorded but cannot block;
- only trusted non-repository managed policy can grant blocking/fail-closed authority, and only at eligible points/codes;
- repository declarations remain disabled until exact external approval and become stale after identity/configuration changes;
- executable handlers receive no shell interpolation, ambient environment, unbounded output, orphan process, or command-line secret;
- HTTP handlers enforce endpoint/redirect/size/timeout/retry/secret-origin policy;
- MCP and extension handlers use existing policy/leases/budgets and cannot self-authorize;
- malformed, unknown-version, oversized, late, duplicated, or mismatched results have deterministic effective failure behavior;
- timeouts, cancellation, unavailable handlers, revocation, and budget exhaustion obey effective fail mode without fabricated success;
- hook chains cannot recurse, trigger unbounded fan-out, or duplicate effects on interruption/resume;
- payload and secret scope is the least-privilege intersection and raw sensitive content is absent without an explicit grant;
- events, logs, persistence, diagnostics, TUI, and CLI expose no secrets or unbounded handler content;
- interactive and headless management/audit decisions are equivalent;
- existing model, tool, planning, mutation, validation, extension, MCP, execution, agent, and skill suites pass unchanged;
- dependency direction and external-SDK/type-isolation gates pass.

## 17 Security and Permissions

All handler declarations, targets, outputs, and advice are untrusted input. Executable handlers and in-process extensions can execute code with their process permissions; timeouts and bounds are resource controls, not sandboxes. HTTP and MCP handlers disclose approved data to external systems. The UI and audit must state these facts before approval.

Repository content cannot enable itself, establish trust, obtain secrets, request fail-closed behavior effectively, or grant blocking authority. Signatures and trusted installation establish identity/integrity, not safety or host authority. Managed policy must name immutable handler identity, eligible points, denial codes, failure mode, data scope, secret scope, and override rules.

No hook result is an approval. No hook can broaden authority or cause a repository side effect outside the existing owning host subsystem. Existing hard guardrails—path confinement, exact diffs, mutation policy, validation, destructive-Git denial, secret redaction, model/tool policy, and cancellation—remain invariant.

## 18 Observability

Create one span per coordinator boundary and child spans per handler invocation, correlated to session, run, operation, hook, handler, adapter, attempt, and generation IDs. Record eligibility, authority source, effective fail mode, bounded input/output sizes, duration, retry, cancellation, timeout, advice count, denial code, and final host decision.

Metrics include configured/eligible/enabled handlers by type and authority, invocation latency, timeouts/failures/denials, fail-open continuations, fail-closed blocks, stale repository approvals, output truncation/rejection, recursion suppression, budget exhaustion, and adapter availability. Logs omit raw prompts, arguments, diffs, outputs, secrets, authorization headers, environment values, and private handler content.

## 19 Migration and Compatibility

Add ordered migrations for external repository approvals and durable audit/reconciliation state. Existing repositories, sessions, providers, tools, extensions, MCP profiles, plans, workflows, and events operate unchanged with no handlers configured. Hook support is opt-in and compiled defaults contain no repository-approved handler.

Unknown declaration/envelope/result versions remain inspectable but unavailable. Handler identity or configuration changes require re-evaluation and, for repository handlers, fresh approval. Event restoration tolerates unknown future hook events according to existing migration rules without invoking handlers during replay.

## 20 Acceptance Criteria

- All listed lifecycle points expose stable versioned host-owned envelopes at the documented authoritative boundaries.
- Executable, HTTP, MCP, and extension handlers can produce bounded typed acknowledgements, advice, denials, and failures through one coordinator.
- Hooks are advisory by default; advisory/repository handlers cannot block or approve host work.
- Blocking and fail-closed behavior requires explicit trusted managed policy outside repository control and is limited to eligible pre-action points and denial codes.
- Repository hook declarations remain disabled until an explicit external user approval binds the exact repository, handler configuration digest, target, points, limits, secret-reference names, and advisory authority.
- Every invocation enforces cancellation, timeout, input/output, concurrency, retry, aggregate budget, data scope, secret scope, redaction, and recursion controls.
- Audit records explain eligibility, trust, authority, effective failure behavior, granted scopes, normalized result, and resulting host transition without exposing secrets.
- Interruption/restoration never blindly replays a handler or duplicates a host effect.
- Interactive and headless users can inspect, approve/revoke, enable/disable, test, and audit hooks through the same application boundary.
- Scenario M, adapter/policy/security/persistence/architecture tests, documentation, manual cases, and DOX pass.

## 21 Risks

- **Hooks become an alternate plugin or workflow engine:** keep results closed and non-executable; route all effects through existing owners.
- **Advisory callbacks acquire accidental veto power:** separate declared intent from effective managed authority and record both.
- **Repository configuration self-enables code:** store exact approval externally, make it advisory-only, and invalidate on any approved fingerprint change.
- **Fail-closed outages halt all work:** permit it only under explicit managed policy, bounded points, disclosed failure modes, and observable recovery guidance.
- **Sensitive payloads leak externally:** default to metadata, compute least-privilege data/secret scope, validate origins, and redact every durable surface.
- **Callback recursion or event storms:** closed points, suppression tokens, depth limits, deterministic budgets, and generation fencing.
- **Executable/extension isolation is overstated:** clearly disclose that process timeouts and collectible load contexts are not security sandboxes.
- **Resume duplicates external effects:** use operation IDs, idempotency declarations, durable outcomes, and fail-closed reconciliation rather than blind replay.

## 22 Documentation

Implementation must add/update:

- an ADR for advisory hooks and managed blocking authority;
- hook authoring/adapter schemas and typed payload/result reference;
- operations guidance for installation, trust, repository approval/revocation, secrets, fail modes, testing, audit, and recovery;
- security documentation explaining executable/in-process/external-network trust boundaries;
- `.threadsmith/config.example` with declarations but no approval/trust/secret values;
- event catalog, persistence/restoration, diagnostics, user guide, README, manual tests, and acceptance Scenario M;
- milestone/index/status and affected `AGENTS.md` ownership/index entries.

Planned behavior must not appear as currently available before M13 lands.

## 23 Decisions

- M13 is Plan 40 and follows the stable event, policy, execution, extension, MCP, and skills foundations.
- Hooks are a host-owned typed coordination layer, not arbitrary event subscribers or an alternate action engine.
- Advisory is the universal default. Blocking authority exists only through explicit managed non-repository policy at eligible pre-action points.
- Repository handlers require exact external approval, remain advisory-only, and become unapproved when their fingerprint changes.
- Handler results cannot rewrite inputs/results, grant authority, approve work, or directly request effects.
- Executable, HTTP, MCP, and extension implementations are adapters behind one contract and one policy/audit coordinator.
- Fail-open/fail-closed is declared and audited, but fail-closed is effective only with managed blocking authority.
- Secret and data disclosure is computed per invocation as a least-privilege intersection.
- Hook invocation is bounded, cancellable, deterministic, recursion-safe, and never blindly replayed after interruption.
