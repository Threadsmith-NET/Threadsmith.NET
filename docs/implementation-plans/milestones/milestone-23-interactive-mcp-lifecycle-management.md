## Milestone 23 - Interactive MCP Lifecycle Management  *(plan 59)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make existing MCP profiles, transports, OAuth, and imported capabilities fully operable through one host-owned interactive/headless lifecycle surface without permanently expanding model-visible tool metadata.

**Deliverables:**
- One `IMcpManager` authority shared by startup auto-connect, `/mcp`, headless commands, session transitions, and shutdown.
- Bounded profile list/detail projections covering disconnected and live profiles, source eligibility, transport/trust/auth state, capability counts, timing, and sanitized outcomes.
- Serialized idempotent connect/disconnect/reconnect with generation fencing, invocation-admission closure before atomic registry removal, cancellation, and one remaining-deadline-bounded process-tree drain/kill.
- Complete bounded inspection of server tools, resources/templates, prompts, capability metadata, and schema identities without ordinary-context injection.
- Individual imported-tool enable/disable through the existing Plan-27 availability authority plus a repository/schema-bound user-owned approval input outside repository control; resources/prompts remain explicit untrusted host operations rather than model tools.
- Explicit MCP resource read and prompt get operations with arguments, MIME/output/time bounds, provenance, and no instruction authority.
- OAuth authenticate, local logout, supported remote revoke, and one-identity switch/re-auth with honest static-token/unsupported/unconfirmed outcomes.
- Structured sanitized diagnostics for configuration, policy, network/executable, auth, handshake, discovery, capability translation, registry, latency, and drain/kill failures.
- Keyboard-friendly interactive selectors, exact headless parity/confirmation/exit codes, Scenario Y, ADR-45, tests, documentation, manual verification, and DOX.

**Exit criteria:**
- `/mcp list|inspect` shows every effective profile—including non-auto-connect definitions—with truthful bounded state and no secret/server-content leakage.
- Eligible profiles connect, disconnect, and reconnect through one serialized lifecycle authority; cancellation, failures, session transitions, shutdown, and hung processes leave no stale registry entries or transport handles.
- Tools/resources/templates/prompts are inspectable on demand but absent from unrelated model context; only individually enabled tools enter canonical model schemas.
- Tool availability is profile/capability/schema identity-bound and fails closed after server capability changes.
- Resource and prompt operations are bounded, policy-governed, and always untrusted evidence rather than instructions or implicit tools.
- OAuth authenticate/logout/revoke/switch preserve Plan-23 security, distinguish local from remote effects, retain one identity only, and never delete static external secrets.
- Diagnostics report structured actionable failure and honestly measured latency phases without arbitrary tool calls, raw payloads, credentials, callback data, or account claims.
- Interactive and headless surfaces use identical manager outcomes, confirmation rules, cancellation, and stable error classifications.
- Existing auto-connect, transports, OAuth, tool policy/availability, activity, canonical context, restoration, scheduling, redaction, and SDK-isolation suites remain compatible.
- Focused automated/real/opt-in coverage, ADR-45, Scenario Y, user/operations/manual docs, status, and DOX pass.

**Implementation evidence:**
- `McpManager` and Core MCP management DTOs provide the shared authority and stable result classifications.
- SDK transports normalize tools/resources/templates/prompts, debounce advertised list changes into atomic bounded replacements, and redact explicit untrusted content before Core projection.
- MCP tool preferences bind Plan-27 availability to the capability digest; repository rebinding invalidates live generations, and identity lifecycle is exact-profile scoped with same-origin non-redirecting revocation.
- `/mcp` and `--mcp` dispatch the same command; `Threadsmith.McpLifecycle.Tests` includes a real full-capability stdio fixture.
- ADR-45, Scenario Y, MTP-235–239, the user guide, and MCP operations guide own maintained behavior and remaining live/terminal checks.

**Prerequisites:** plans 08, 18-23, 27, 35, 40, 49, 51, 56-57, and 62.

**Scope decisions:**
- `/mcp` is a user lifecycle surface, not a model tool.
- One manager owns startup, interactive, headless, transition, and shutdown paths.
- Individual tools reuse Plan-27 availability; no MCP-specific enablement authority.
- Resources/prompts are inspected or invoked explicitly and never automatically advertised/injected.
- `switch-account` replaces the one cached profile identity; concurrent multi-account storage and dynamic registration remain excluded.
- Logout and remote revocation are distinct; diagnostics never invoke arbitrary server tools.
- Repository configuration cannot self-trust, auto-connect, authenticate, enable capabilities, or grant secrets.

---

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
