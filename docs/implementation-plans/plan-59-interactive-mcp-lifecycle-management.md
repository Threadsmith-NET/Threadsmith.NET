# Plan 59 — Interactive MCP Lifecycle Management

**Milestone:** M23 — Interactive MCP Lifecycle Management

**Prerequisites:** plans 08, 18–23, 27, 35, 40, 49, 51, 56–57, and 62

**Depends on by:** future MCP resource/prompt workflows, multi-account identity support, and managed remote MCP workers

**Status:** Implementation complete; maintained live-adapter/real-terminal closeout pending.

## 1 Objective

Make the implemented MCP transports, profiles, OAuth, and imported capabilities usable through one host-owned interactive and headless lifecycle surface. Add `/mcp` commands for bounded profile/status discovery, connect/disconnect/reconnect, capability inspection and individual enablement, OAuth authenticate/logout/revoke/switch, and sanitized diagnostics including handshake/capability/latency information.

The surface reuses `IMcpAdapter`, trusted profile configuration, OAuth token storage, the shared tool registry/availability policy, operation activity, and drain/kill semantics. It does not turn server-provided resources or prompts into permanently advertised model tools, broaden repository authority, expose secrets/raw SDK objects, or make the TUI a second lifecycle authority.

## 2 Architectural Context

Plans 19 and 21–23 provide host-owned MCP profiles, `IMcpAdapter`, stdio/SSE/streamable-HTTP transports, capability DTOs, shared-registry tool publication, best-effort auto-connect, bounded shutdown, static credentials, and one cached OAuth identity per profile. Plan 27 owns repository tool availability and `/tools`; Plan 49 owns truthful operation/MCP activity timing; Plan 51 owns canonical model tool identity; Plan 56 owns serialized session transitions and safe boundaries; Plan 57 owns concurrency claims. Plan 62 owns static-secret discovery and provider-neutral resolution; MCP lifecycle management consumes that boundary and does not add another secret lookup path.

Before Plan 59, composition auto-connected eligible trusted profiles but exposed no `/mcp` lifecycle command. Profiles with `autoConnect: false` have no ordinary user path. The adapter status omits defined-but-disconnected profiles, capabilities are retained only in connect results, SDK transport mapping currently enumerates tools rather than complete resources/prompts, and OAuth logout requires manual token-cache surgery. Plan 59 closes these user-surface and lifecycle-state gaps without weakening the existing adapter/SDK isolation boundary.

## 3 Scope

- One host-owned `IMcpManager` application boundary shared by TUI, slash commands, headless commands, hooks/policy integration, and tests.
- Bounded listing and inspection of all effective trusted profile definitions, including disconnected profiles, configuration source/eligibility, transport, trust, auto-connect, allowed capability kinds, authentication mode, sanitized endpoint/command identity, current state, capability counts, timing, and last sanitized outcome.
- Serialized, idempotent connect, disconnect, and reconnect with stale-generation fencing, in-flight drain/kill behavior, cancellation, safe-boundary coordination, and registry cleanup/publication.
- Complete SDK-backed discovery and host-owned metadata projection for tools, resources, resource templates where supported, and prompts permitted by profile policy.
- Bounded capability list/detail inspection with sanitized descriptions, argument schemas, MIME/URI-template metadata, and server capability summary.
- Individual imported-tool enable/disable through the existing Plan-27 repository availability authority; disabled tools are not model-advertised or invocable.
- Explicit resource read and prompt argument/render operations through host application commands, with separate policy/approval/evidence handling and no automatic model-tool advertisement.
- OAuth authenticate, local logout, standards-based remote revocation when advertised and supported, and switch-account-as-logout/re-authentication for the existing one-identity-per-profile contract.
- Sanitized lifecycle diagnostics covering profile eligibility, discovery/handshake, policy, endpoint, capability translation, registry conflicts, authentication state, last error, durations/latency, and drain/kill outcome.
- Interactive selectors and keyboard-friendly numbered choices plus deterministic interactive-text/headless-JSON parity and exit codes.
- Persistence/restoration of non-secret lifecycle preferences and status history where required, while live transports, process IDs, tokens, capabilities, and connection authority are revalidated rather than blindly restored.
- Tests, operations/user documentation, manual verification, Scenario Y, ADR-45, and DOX.

## 4 Non-Scope

- MCP server authoring, editing profile configuration, dynamic client registration, stdio OAuth, or arbitrary OAuth discovery overrides.
- Concurrently retained multiple accounts per profile, account-name discovery, or an account vault. `switch-account` clears the current profile identity and performs a new authorization flow.
- Guaranteeing remote token revocation when the authorization server does not advertise/support RFC 7009-compatible revocation. Local logout remains distinct and honest.
- Turning every MCP resource or prompt into a model-visible tool, injecting all server capability metadata into every request, or bypassing canonical tool/availability selection.
- Repository configuration granting trust, auto-connect authority, authentication, capability enablement, secrets, or lifecycle approval beyond existing trusted configuration rules.
- Browser automation, authenticated arbitrary web fetching, MCP marketplace/discovery, server installation, or remote worker management.
- A second adapter implementation in the TUI/CLI or direct terminal access to SDK/token-store types.

## 5 Current State

Threadsmith composes one `McpManager` for trusted-profile auto-connect, TUI/headless dispatch, and shutdown. Core owns closed lifecycle request/result, profile/capability/content/authentication/diagnostic/latency DTOs; `Threadsmith.Mcp` retains SDK, transport, OAuth, HTTP, and live-handle details. Per-profile gates, a bounded global connection limiter, connection- and capability-generation fencing, idempotent transitions, registry-first removal, tracked in-flight tool/resource/prompt requests, and bounded drain/kill govern lifecycle.

The pinned SDK mapping discovers allowed tools, fixed resources, resource templates, and prompts into bounded digest-bearing host descriptors. Tools publish disabled by default and reuse Plan-27 availability with versioned schema identity. Exact resource/template reads and prompt rendering return bounded explicitly untrusted content with aggregate truncation disclosure and without model-context admission. `McpIdentityManager` supports coarse cache state, atomic exact-prefix local logout, request-timeout-bounded advertised HTTPS RFC 7009 revocation with unsupported/unconfirmed outcomes, explicit local-only cleanup after network failure or timeout, and logout-or-revoke switch/re-auth while static credentials remain external.

`/mcp` supplies list, inspect, connect, disconnect, reconnect, capability list/detail, enable/disable, resource read, prompt get, auth, logout, revoke, switch-account, and diagnose with numbered selectors and exact confirmations. `--mcp` exposes the same manager via stable JSON, exact IDs, confirmation flags, and stable exit classes. The extended real stdio fixture and `Threadsmith.McpLifecycle.Tests` cover the focused contract; explicitly opted-in live HTTP/OAuth/revocation and maintained real-terminal/race/privacy checks remain in MTP-235–239.

## 6 Proposed Design

### 6.1 Single lifecycle authority

Add `IMcpManager` above `IMcpAdapter`. It receives the immutable effective profile catalog from trusted configuration, adapter, tool availability authority, policy/hooks, activity sink, OAuth identity manager, session transition/safe-boundary coordinator, time provider, and sanitizer. TUI/headless layers issue host-owned commands and render host-owned projections only.

Maintain one serialized transition lane per profile plus a bounded global connection limiter. Each operation captures profile-catalog, policy, repository, session, credential, and registry generations. Conflicting connect/disconnect/reconnect/auth transitions for the same profile serialize; duplicate requests return a stable already-connected/disconnected or operation-in-progress outcome. Session/repository transitions either await a safe lifecycle boundary or cancel/drain according to Plan 56—never leave imported capabilities registered without their live connection generation.

Reconnect means bounded disconnect followed by a fresh profile/secret/policy/DNS/auth/discovery evaluation. It never reuses a live SDK session or stale server capability snapshot.

### 6.2 Profile and connection projection

Merge configured profiles with live adapter state so `/mcp list` shows disconnected, connecting, connected, draining, killed, failed, and policy-ineligible definitions. Projection fields are closed and bounded:

- stable profile ID and sanitized display name;
- trusted configuration source class, transport, trust, auto-connect, and allowed capability kinds;
- sanitized stdio executable basename or HTTP origin (no path/query/headers/arguments/environment/secrets by default);
- authentication mode and coarse identity state (`NotApplicable`, `SignedOut`, `Cached`, `Refreshing`, `AuthenticationRequired`, `Authenticated`, `RevocationUnsupported`, `Failed`) without account claims or tokens;
- live state, generation, connected-since/last-transition times, in-flight count, process-presence boolean (PID only in privileged diagnostics), capability counts, enabled tool count, and last sanitized failure classification;
- startup/handshake/discovery and bounded sampled request latency where available.

`/mcp inspect <profile>` adds bounded configured timeout/policy details, sanitized redirect/auth metadata, capability summaries, and diagnostics. It never prints secret references unless explicitly safe identifiers are already user-configured, and never prints resolved values.

### 6.3 Commands and selectors

Interactive grammar:

- `/mcp` or `/mcp list`
- `/mcp inspect [profile]`
- `/mcp connect [profile]`
- `/mcp disconnect [profile]`
- `/mcp reconnect [profile]`
- `/mcp capabilities [profile] [kind]`
- `/mcp capability [profile] [capability]`
- `/mcp enable [profile] [tool]`
- `/mcp disable [profile] [tool]`
- `/mcp resource read [profile] [resource]`
- `/mcp prompt get [profile] [prompt]`
- `/mcp auth [profile]`
- `/mcp logout [profile]`
- `/mcp revoke [profile]`
- `/mcp switch-account [profile]`
- `/mcp diagnose [profile]`

Missing or ambiguous IDs use bounded numbered selectors. Destructive identity actions show exact profile/origin and local-versus-remote effect before confirmation. Commands never become model tools merely because the interactive surface exists.

Headless commands expose the same operations with exact IDs, noninteractive authorization callback behavior inherited from Plan 23, explicit confirmation flags for logout/revoke/switch, stable JSON projections matching the interactive text outcomes, and documented exit codes. Any `--mcp` action bypasses optional extension startup—even when `--tui` is also present—so no extension diagnostic can precede the single JSON envelope. No prompt fallback occurs in headless mode.

### 6.4 Capability discovery and retention

Extend SDK transport mapping to enumerate server-advertised tools, resources/resource templates, and prompts using the pinned official SDK where supported. Normalize immediately into bounded host-owned descriptors containing stable profile-qualified ID, kind, server name, sanitized description, and kind-specific safe metadata. Reject duplicates, invalid names/URIs/templates/schemas, oversized metadata, and unsupported content before registry publication.

The manager retains immutable descriptors only for the active connection generation. Disconnect atomically removes tools from the shared registry and clears resource/prompt handles/descriptors from the live catalog. User-facing snapshots may retain bounded counts/digests/last outcome, never callable handles or unbounded server metadata.

Capability listing is complete within a strict 256-item connection bound and loaded only on user inspection. It is not appended to ordinary model context. Server `listChanged` notifications, when advertised by the negotiated server capabilities, trigger debounced complete rediscovery, atomic registry replacement, capability-lease invalidation for every pre-resolved replaced proxy, and a manager snapshot-generation advance; otherwise reconnect is the refresh boundary.

### 6.5 Individual tool enablement

Imported tools continue to register with stable canonical IDs and MCP source metadata. Initial effective availability follows existing profile capability policy plus Plan-27 repository tool availability and an exact repository-bound user approval stored outside repository control. `/mcp enable|disable` delegates to the same `ToolStateManager` authority used by `/tools`; the user-owned approval record is a backing trust input, never a parallel runtime setting.

A disabled MCP tool remains inspectable but is absent from model schemas and denied at invocation. Enablement is identity-bound to profile ID plus stable server capability identity/schema digest. Reconnect with a changed schema/name/digest invalidates stale enablement and requires review/re-enable according to existing dynamic-tool safety policy. Repository-controlled `tools:enabled` or `tools:defaultEnabledOverrides` values may narrow availability but cannot create the repository-bound user approval needed to enable externally supplied tools or elevate their trust/effect classification.

Resources and prompts are not enabled as model tools. Their explicit host operations are separately bounded and policy-checked.

### 6.6 Resources and prompts

`resource read` accepts only a discovered exact resource ID or a validated resource-template expansion supplied by the user through a bounded typed selector. The adapter invokes the SDK resource operation with request timeout/cancellation, MIME/size limits, sanitization, and network/secret policy. Text results become untrusted MCP evidence; binary/unsupported content is rejected or represented only by safe metadata unless a later plan defines an artifact contract.

`prompt get` presents required/optional arguments, accepts bounded user values, invokes the server prompt operation, and renders the returned messages/content as untrusted external prompt material. It is never treated as system/developer instruction and is not automatically injected into a model request. A separate future governed action would be required to admit selected prompt content to conversation context.

Both operations record profile/capability/generation/provenance and activity duration without content/secret leakage.

### 6.7 Authentication lifecycle

Factor Plan-23 token operations behind a host-owned identity manager:

- `auth`: connect/authenticate or force an explicit authorization flow when signed out; never silently launches a browser from listing/diagnostics.
- `logout`: disconnect/drain first, remove all local token/expiry/metadata entries for the exact profile namespace atomically, invalidate credential/connection generations, and leave remote grants unchanged.
- `revoke`: disconnect, read bounded metadata without redirects, attempt an advertised same-origin HTTPS revocation endpoint using the applicable token under a dedicated client with no ambient credentials, then clear local tokens after a confirmed success; transient/ambiguous remote failure reports `RemoteRevocationUnconfirmed` and requires explicit user choice before local-only cleanup.
- `switch-account`: confirm, perform logout or confirmed revoke according to user choice, then start a fresh authorization flow with account-selection hints only if standards/provider metadata safely supports them. It replaces rather than retains the one cached identity.

State/issuer/PKCE/scope caps, callback safety, secret cache permissions, redaction, HTTP-only OAuth, and pre-registered client requirements remain unchanged. Logout/revoke never deletes static-token secrets; static-auth profiles receive a clear unsupported outcome and guidance to rotate/remove the external secret.

### 6.8 Diagnostics and latency

Build diagnostics from structured lifecycle observations rather than scraping logs. `/mcp diagnose` performs only user-selected bounded checks:

1. profile configuration/trust/source eligibility;
2. executable/working-directory or endpoint/network-policy preflight;
3. secret-reference presence without resolution display;
4. OAuth metadata/cache coarse state;
5. connection/handshake and discovery result;
6. capability translation/filter/registry collision outcome;
7. optional protocol ping or harmless SDK-defined health operation when advertised—never invoke an arbitrary server tool;
8. disconnect/drain state.

Diagnostics distinguish configured timeout, measured startup/handshake/discovery/ping durations, and recent imported invocation latency derived from Plan 49. No fabricated “server latency” is shown when transport phases cannot be separated. Values use monotonic elapsed time, bounded recent aggregates (count/min/max/mean or percentiles only with sufficient samples), and explicit unavailable markers.

Sanitize stderr, endpoint, OAuth, SDK, schema, and policy failures. Provide actionable classifications without headers, arguments, environment, callback URLs, tokens, authorization codes, raw claims, resource contents, prompt contents, or arbitrary server payloads.

### 6.9 Policy, hooks, persistence, and restoration

Connect/reconnect/auth/resource/prompt/revoke operations pass existing repository trust, profile source, network/executable, secret scope, approval, lifecycle hook, timeout, cancellation, activity, and audit boundaries. Hooks may advise or managed policy may deny; they cannot grant profile trust, secrets, token authority, capability enablement, or suppress drain/revocation outcomes.

Persist only user-owned individual tool availability through the existing owner and bounded lifecycle audit/status data where current architecture supports it. Do not persist live connection states as authority. On restart/resume, profiles are disconnected until ordinary auto-connect or explicit connect revalidates current configuration, trust, credentials, endpoint, and capability schemas. Session clone does not clone live MCP connections or OAuth identities; user-level token cache remains independently available by exact profile identity.

## 7 Public Contracts

Add provider-neutral immutable records/interfaces for `IMcpManager`, lifecycle commands/results, effective profile summaries/details, authentication state/result, capability descriptors/details, resource-read/prompt-get requests/results, diagnostic checks/report, latency summary, and explicit failure classifications.

Extend `IMcpAdapter`/`IMcpTransport` only as required for complete capability enumeration, resource/prompt operations, ping/health when safely supported, and immutable live-generation snapshots. Preserve SDK isolation: MCP SDK, HTTP, process, OAuth-library, terminal, persistence, and raw server types do not cross the boundary.

All public members follow XML documentation, nullable, cancellation, collection, and host-owned DTO guardrails.

## 8 Project/File Changes

- `Threadsmith.Mcp` — manager/adapter contracts and implementation, full capability normalization, resource/prompt operations, identity lifecycle, diagnostics, latency, generation fencing, and SDK isolation.
- `Threadsmith.Tools` — shared imported-tool availability identity/digest integration only where Plan 27 ownership requires it; no duplicate MCP availability store.
- `Threadsmith.Core` / `Threadsmith.Execution` — application commands/events/projections and serialized safe-boundary coordination only where existing ownership requires it.
- `Threadsmith.Persistence` — bounded lifecycle/audit projection or migration only if implementation inspection proves necessary; never tokens/live handles/raw capability bodies.
- `Threadsmith.App` — one composed `IMcpManager` for auto-connect, interactive, headless, shutdown, and session transitions.
- `Threadsmith.Tui` — `/mcp` parser, bounded selectors, confirmations, semantic rendering, cancellation, and responsive activity.
- `Threadsmith.Cli` or current headless owner - exact-ID command parity, confirmations, stable JSON output matching interactive text outcomes, and exit codes.
- `Threadsmith.Telemetry` — sanitized lifecycle/diagnostic/latency projection and bundle coverage.
- MCP test server and M9/M10/new M23 tests — tools/resources/templates/prompts, list-change, auth/logout/revocation, failures, latency, drain, and real transport fixtures.
- ADR-45, Scenario Y, `docs/user-guide.md`, `docs/operations/mcp-connections.md`, manual test plan, command/keyboard docs, event catalog/configuration docs if affected, plan/status/index/DAG, and DOX.

Any new fixture copied to output uses `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Inspect current profile loading/source precedence, adapter lifecycle, SDK capability APIs, OAuth token store, shared tool availability identity, activity timing, safe-boundary composition, and headless command conventions.
2. Add ADR-45 for one MCP lifecycle authority, capability inspection versus model advertisement, one-identity switch semantics, local logout versus remote revocation, and resource/prompt trust treatment.
3. Define manager commands/results, profile/auth/capability/resource/prompt/diagnostic/latency projections, closed failure kinds, generation identities, and bounds.
4. Implement effective-profile catalog projection merging disconnected trusted definitions with live adapter state and source/policy eligibility.
5. Implement per-profile serialized connect/disconnect/reconnect, idempotence, global limiter, registry atomicity, cancellation, stale-generation handling, and Plan-56 safe boundaries.
6. Extend SDK mapping and test server to enumerate/normalize bounded tools, resources/templates, and prompts; handle duplicates, invalid schemas/URIs, list-change, and disconnect cleanup.
7. Delegate individual imported-tool enable/disable to Plan-27 availability using profile/capability/schema identity; prove disabled tools are absent/denied and stale enablement fails closed.
8. Add bounded resource read and prompt get adapter/manager operations with untrusted evidence, MIME/schema/argument/result bounds, policy, timeout, and no automatic context/tool exposure.
9. Refactor OAuth cache access behind identity lifecycle operations; implement authenticate, atomic local logout, conditional standards-based remote revoke, honest unsupported/unconfirmed outcomes, and one-identity switch/re-auth.
10. Add structured diagnostic checks and monotonic phase/recent-latency summaries using Plan-49 observations, with no arbitrary tool invocation.
11. Add `/mcp` grammar, selectors, confirmations, semantic output, cancellation/activity, and context-sensitive guidance.
12. Add exact-ID headless parity, automatic-connection OAuth callback suppression, explicit headless OAuth instructions on standard error, confirmation flags, a single stable JSON standard-output projection matching interactive text outcomes, and stable exit codes.
13. Integrate hooks/policy/audit/restoration/clone/shutdown and diagnostics/redaction; add persistence migration only if inspection requires it.
14. Add deterministic unit/integration/end-to-end/security/privacy/architecture tests, real stdio fixture coverage, environment-gated live HTTP/OAuth checks, and Scenario Y.
15. Update implementation docs, user/operations/manual guidance, current status, configuration/event docs where affected, and complete root/docs/source/test DOX.

## 10 Testing

Automated coverage must verify:

- all effective trusted profiles appear when disconnected, while repository-owned/self-trusting or invalid profiles are visibly ineligible and cannot connect;
- connect/disconnect/reconnect are serialized, idempotent, cancellation-safe, generation-fenced, and atomically publish/remove registry capabilities;
- duplicate lifecycle requests, startup failure, partial discovery, registry collision, disconnect failure, hung stdio drain/kill, independently attempted all-resource HTTP disposal after deadline expiry, shutdown, and session transition leave no orphan process/client/tool;
- capability lists/details are complete within the strict connection bound, sanitized, and absent from unrelated model context/tool schemas;
- tools/resources/templates/prompts are enumerated only when allowed, invalid/duplicate/oversized metadata fails closed, and list-change cannot race a stale generation into the registry;
- individual tool disablement removes model advertisement and denies invocation through the existing availability authority; changed capability/schema identity invalidates stale enablement and every pre-resolved proxy lease from the replaced generation;
- resources/prompts never become model tools; bounded reads/gets enforce exact discovery identity, arguments, MIME/output/time policy, preserve aggregate truncation when items are omitted, and return only untrusted evidence;
- auth never launches from automatic startup/rebind, list, inspect, or diagnose; cached automatic OAuth may refresh without UX, explicit login preserves PKCE/state/issuer/scope/callback constraints, and headless OAuth instructions cannot contaminate the single JSON stdout envelope;
- logout disconnects first, atomically clears only the exact profile namespace, invalidates generations, and never claims remote revocation;
- revoke uses only advertised/supported endpoints, distinguishes success/unsupported/unconfirmed failure, leaks no token, and handles local cleanup after network failure or host timeout according to explicit user choice;
- switch replaces one cached identity and retains no second-account state; cancellation/failure cannot reconnect with mixed old/new credentials;
- static-token profiles reject logout/revoke/switch without deleting external secrets;
- diagnostics distinguish eligibility, preflight, auth, handshake, discovery, translation, registry, policy, timeout, cancellation, and drain failures without secrets/raw payloads;
- timings use monotonic elapsed boundaries, label unavailable phases honestly, and bounded aggregates cannot identify secret/user data;
- interactive selectors/confirmations and headless exact-ID/confirmation/exit-code behavior share the same manager outcomes;
- repository config, MCP content, model output, hooks, extensions, or trust cannot self-connect, authenticate, enable a tool, expand secrets, revoke, or bypass policy;
- restart/resume/clone never restore live authority or stale handles and auto-connect still follows trusted source rules;
- canonical tool identity/cache behavior and Plan-57 scheduling remain deterministic across enablement, reconnect, schema change, and original-order continuations;
- tokens, headers, environment, arguments, callback URLs, codes, raw claims, resource/prompt contents, server stderr, and unsafe schemas do not leak through events/logs/status/diagnostics/bundles;
- M8–M10 and Plan-27/49/51/56/57 regression and SDK-isolation suites remain green;
- maintained real stdio plus explicit opt-in HTTP/OAuth lifecycle checks cover connect, inspect, latency, logout/re-auth, reconnect, and clean shutdown.

## 11 Security/Permissions

MCP profile configuration and server output are untrusted unless their source and trust were established by existing host policy. Interactive convenience grants no new authority. Every lifecycle action revalidates current profile identity/source/trust, repository policy, secrets, executable/network host, authentication mode, and generation.

Server names, descriptions, schemas, resources, prompts, errors, stderr, OAuth metadata, and timings are untrusted and bounded. They cannot become instructions, enable themselves, request additional secrets, authorize another endpoint, or alter approval. Resource/prompt data is external evidence, not system prompt material.

Logout, revoke, and switch are sensitive identity mutations requiring exact profile selection and confirmation. Token cache operations are namespace-confined, atomic, owner-protected, and redacted. Remote revocation is never claimed without a confirmed protocol outcome. Stdio processes retain curated-environment and drain/kill controls; HTTP/SSE retain endpoint/network/credential isolation.

## 12 Observability

Emit host-owned lifecycle activity for list/inspect only when useful and for connect, disconnect, reconnect, discovery, resource/prompt operations, authenticate, logout, revoke, switch, and diagnose with source, profile ID, transport, sanitized origin/display identity, outcome, monotonic duration, capability counts, and failure classification.

Expose bounded handshake/discovery/ping/recent-invocation timing with explicit measurement labels. Never log/project tokens, secret values/references where sensitive, headers, environment, arguments, callback URLs/query, authorization codes, claims/account identifiers, full endpoint query/path, raw schemas beyond bounded explicit inspection, resource/prompt content, or unsanitized SDK/server errors.

Diagnostic bundles include bounded redacted lifecycle summaries and canary verification, not credentials or content.

## 13 Migration/Compatibility

Existing profiles, auto-connect, transports, imported tools, OAuth tokens, and invocation IDs remain compatible. `/mcp` is additive. The manager becomes the single composition path for startup auto-connect and shutdown so interactive/headless operations cannot diverge.

Existing one-identity token namespaces remain valid. No automatic logout, revocation, or account migration occurs. Capability descriptors gain additive kind-specific metadata; tools retain stable IDs where server identity/schema is unchanged. New resource/prompt operations are unavailable when the pinned server/SDK does not advertise them.

Individual imported-tool availability uses Plan-27 repository settings only as a narrowing input and stores the granting repository/profile/capability/schema approval in a bounded user-owned file outside repository control. Changed identities or repositories default disabled/review-required rather than silently enabling. Live connections, process IDs, capability handles, diagnostics, and auth-flow state are never restored as authority.

## 14 Acceptance Criteria

- `/mcp list|inspect` and headless equivalents show every effective profile and truthful sanitized state, including profiles not configured for auto-connect.
- A user can connect, disconnect, and reconnect any eligible profile through one serialized host manager; registry publication/removal, drain/kill, cancellation, session transitions, and shutdown are correct.
- Tools, resources/templates, and prompts can be inspected with bounded metadata; only enabled tools enter model schemas, while resource/prompt operations remain explicit untrusted host actions.
- Individual imported tools use the existing availability authority and stale schema/profile identities fail closed.
- OAuth profiles support explicit authenticate, local logout, honest supported remote revocation, and one-identity switch/re-auth without secret or account leakage; static-token profiles are handled truthfully.
- `/mcp diagnose` identifies configuration, policy, auth, handshake, discovery, translation, registry, timeout, and drain failures and reports measured capability/latency data without invoking arbitrary tools.
- Interactive and headless surfaces have behavior/output parity, bounded selectors, exact confirmations, stable failure codes, and no duplicated lifecycle authority.
- Existing auto-connect, transport, OAuth, tool policy, canonical context, scheduling, restoration, redaction, and SDK-isolation tests remain compatible.
- Focused automated coverage, real/opt-in transport checks, ADR-45, Scenario Y, documentation, manual verification, status, and DOX are current before M23 closes.

## 15 Risks

- **TUI becomes a second authority:** all surfaces call `IMcpManager`; no direct adapter/token/SDK calls.
- **Capability metadata bloats context:** inspection is on demand; only enabled tool schemas use existing canonical advertisement; resources/prompts never become tools automatically.
- **Reconnect races leave stale tools:** serialized profile transitions, generation-bound descriptors, atomic registry replacement/removal.
- **Server capability change preserves unsafe enablement:** bind availability to stable identity/schema digest and fail closed on change.
- **Logout is confused with revocation:** distinct commands/outcomes and explicit remote-support reporting.
- **Switch implies unsupported multi-account storage:** define it as replace-current-identity through logout/re-auth, retaining only one identity.
- **Diagnostics leak credentials/content:** structured allowlisted fields, sanitization, bounds, canary tests, and no raw log scraping.
- **Latency claims are misleading:** label measured phase/source, use monotonic clocks, show unavailable rather than infer.
- **Resource/prompt prompt injection:** untrusted evidence only, no automatic model-context admission or instruction authority.
- **Lifecycle interruption strands processes:** existing bounded drain/kill plus safe-boundary and cancellation tests.

## 16 Documentation

Planning adds this plan, M23 milestone/DAG/index entries, Scenario Y, shared-context registration, and DOX updates. Implementation updates `docs/user-guide.md`, `docs/operations/mcp-connections.md`, manual testing, command/help/keyboard documentation, event/configuration catalogs if contracts change, root/docs/source/test DOX, and current status. Planned commands must not be documented as available before implementation.

## 17 Open Decisions

Resolved for planning:

- One host-owned manager serves startup, TUI, headless, shutdown, and session transitions.
- `/mcp` is a user command surface, not a new permanent model tool inventory.
- Individual tools use the Plan-27 `ToolStateManager`; its user-owned repository/schema approval backing file prevents repository settings from granting MCP authority without creating a parallel runtime enablement authority.
- Resource and prompt inspection/use remain explicit host operations and untrusted evidence, not automatically advertised tools or model instructions.
- `switch-account` replaces the current one-profile identity through logout/re-auth; multi-account storage remains out of scope.
- Local logout and remote revocation are distinct, with honest unsupported/unconfirmed results.
- Diagnostics never invoke arbitrary server tools to measure health.
- Repository profiles cannot grant trust, auto-connect, authentication, enablement, or secrets.

Implementation decisions resolved:

- the pinned SDK directly supplies resources/templates/prompts; ping is attempted only as an optional protocol check and unsupported versions report failure without a tool fallback; advertised tools/resources/prompts list-change notifications debounce into one complete bounded replacement, while reconnect remains the fallback refresh boundary;
- canonical tool IDs remain stable while the Plan-27 persisted preference key includes the MCP tool version derived from its schema digest, so changed schemas fail closed;
- one bounded MCP-local monotonic accumulator retains recent explicit resource/prompt/ping samples, while startup and discovery are honestly labeled as a combined measurement where the SDK does not expose separable phases;
- remote revocation uses only authorization-server metadata-proven HTTPS RFC 7009 endpoints and preserves local identity on unsupported/unconfirmed outcomes unless explicitly directed to clean locally;
- no persistence migration is needed: existing Plan-27 settings and the OAuth cache retain their existing ownership, while live state, descriptors, handles, diagnostics, and latency remain process-local authority.
