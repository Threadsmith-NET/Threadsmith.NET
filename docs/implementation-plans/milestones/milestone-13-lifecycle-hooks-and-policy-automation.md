## Milestone 13 — Lifecycle Hooks and Policy Automation  *(plan 40)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Add typed lifecycle automation at stable host boundaries while keeping the host authoritative. Hooks are advisory by default; only explicit trusted managed policy outside repository control may grant bounded blocking authority.

**Deliverables:**
- Versioned hook points for repository, model, tool, plan, mutation, validation, correction, run, extension, and MCP lifecycle boundaries.
- Host-owned typed invocation envelopes, bounded advice/denial/failure results, immutable handler identities, and deterministic ordering.
- Executable-process, HTTP, MCP, and extension handler adapters behind one coordinator and policy boundary.
- Separate handler trust, enablement, exact repository approval, advisory/blocking authority, and fail-open/fail-closed decisions.
- Per-handler cancellation, timeout, input/output, concurrency, retry, aggregate-budget, recursion, data-scope, and secret-scope enforcement.
- Durable audit events, operation correlation, interruption reconciliation, redacted telemetry, and shared interactive/headless management surfaces.
- Configuration, security/operations guidance, Scenario M, architecture tests, adapter fixtures, and maintained manual cases.

**Exit criteria:**
- Every documented hook fires exactly once at its authoritative boundary with a versioned host-owned payload and stable operation correlation.
- Executable, HTTP, MCP, and extension handlers return only bounded typed acknowledgements, advice, denials, or failures; no result directly performs or approves a host action.
- Advisory and repository handlers cannot block. Blocking/fail-closed behavior is effective only for eligible pre-action points explicitly granted by trusted non-repository managed policy.
- Repository declarations remain disabled until an external user approval binds the exact repository, handler fingerprint, target, points, limits, secret-reference names, and advisory authority; changes invalidate approval.
- Timeouts, cancellation, malformed/oversized output, unavailable handlers, retries, budget exhaustion, and recursion follow deterministic audited behavior without orphan work or duplicate effects.
- Data and secrets use least-privilege per-invocation scopes and never leak through events, logs, persistence, diagnostics, TUI, CLI, process arguments, redirects, or handler errors.
- Restoration never blindly replays a handler, and existing behavior remains unchanged when no handlers are configured.
- Scenario M plus focused policy, adapter, security, persistence, compatibility, and architecture suites pass; documentation, manual cases, and DOX are current.

**Prerequisites:** plans 02, 05, 07–10, 12–20, 27, 30–31, and 37–39.

**Scope decisions:**
- Hooks are typed host-owned lifecycle coordination, not arbitrary event subscriptions, plugins, scripts, or workflows.
- Advisory is the default and repository handlers remain advisory-only.
- Managed policy names immutable handler identity, allowed points/denial codes, fail behavior, data/secret scope, and override rules.
- After/terminal hooks can never retroactively block or roll back completed host actions.
- Hook results cannot rewrite model/tool/plan/mutation/validation data, grant authority, or bypass existing subsystem owners.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
