# Plan 62 — Extensible Secret Discovery

**Milestone:** M22.2 — Extensible Secret Discovery

**Prerequisites:** plans 01, 07–08, 18–20, 23, 27, 31–32, 36, 39–40, 45, 50, 58, and 61

**Depends on by:** plan 59 and future AWS Secrets Manager and other externally backed secret providers

**Status:** Implementation and focused automated coverage complete; maintained cross-platform permission, real-consumer, interruption, and redaction closeout pending.

## 1 Objective

Replace component-specific static-secret lookup with one host-owned, provider-based secret-discovery boundary. Any typed configuration field explicitly declared secret-aware, or any component explicitly holding a validated secret reference, can request a secret through the same resolver and receive a useful sanitized failure when discovery does not succeed.

Ship three initial providers: a repository store under `<repository>/.threadsmith/secrets`, accepted only when Git proves the store is untracked and effectively ignored; a user store under `~/.threadsmith/secrets`; and `THREADSMITH_` environment variables. Preserve environment variables as an optional source rather than the required user workflow, and make future providers such as AWS Secrets Manager addable without changing consuming components.

## 2 Architectural Context

Threadsmith already uses logical `secrets:` references, but resolution and persistence differ by subsystem. `ConfigurationSecretStore` reads the effective configuration view, repository model secrets may use `.threadsmith/secrets/config.json`, trusted web-search settings exclude that repository layer, and OAuth implementations own separate token caches. The inconsistency burdens users and makes provider trust, precedence, diagnostics, and redaction difficult to reason about.

The host must continue to distinguish secret references from values. Secret discovery is a privileged boundary, not ordinary configuration interpolation. Automatically resolving every string that resembles a reference could materialize credentials into model-visible configuration, logs, events, diagnostics, durable projections, or extension data. Therefore configuration resolution is opt-in through typed schema metadata; components may also request a validated reference explicitly.

The extension SDK and governed skills remain untrusted capability surfaces. They may consume only host-authorized secret references through existing scoped invocation contracts; they cannot register secret providers or inspect stores merely by being installed. Initial provider registration is compiled and host-owned. A later plan may define a separately trusted provider-extension contract.

## 3 Scope

- Host-owned immutable secret reference, request context, provider outcome, aggregate resolution result, and sanitized diagnostic contracts.
- `ISecretResolver` as the only ordinary consumer boundary and multi-registration `ISecretProvider` implementations selected by deterministic host-owned policy.
- Secret-aware typed configuration fields that resolve only at the final owning-component boundary.
- Explicit component resolution for already validated logical references.
- Repository JSON secret store under `.threadsmith/secrets`, with confinement, tracked/index-state rejection, and effective Git-ignore proof before use.
- User JSON secret store under `~/.threadsmith/secrets`, outside repository control.
- Environment-variable provider preserving the existing `THREADSMITH_` and `__`-to-`:` mapping.
- Deterministic provider order, duplicate handling, trust/scope constraints, cancellation, bounded I/O, and sanitized actionable failures.
- Migration of static-key consumers, including model providers, web search, MCP static authentication, hooks, NuGet/private-source access, and other configuration-backed credentials.
- Explicit compatibility boundaries for existing OAuth token caches; OAuth token lifecycle is not silently converted into generic static configuration.
- Tests, ADR, Scenario AB, configuration/operations/user documentation when implemented, and DOX.

## 4 Non-Scope

- Storing secret values in ordinary repository/user configuration, command-line arguments, logs, events, model context, execution records, diagnostics, or support bundles.
- Transparently resolving arbitrary strings or every configuration key containing `secret`, `token`, or `password`.
- Secret enumeration or disclosure to models, tools, skills, extensions, MCP servers, hooks, repositories, or terminal projections.
- A repository-controlled provider order, provider implementation, network destination, executable, trust level, or provider enablement decision.
- AWS Secrets Manager, Azure Key Vault, HashiCorp Vault, OS keychains, or other remote/encrypted providers in this initial milestone.
- Treating the repository JSON store as encrypted; ignore enforcement is a precaution, not confidentiality protection.
- Moving OAuth access/refresh tokens without a lifecycle-specific migration and compatibility design.
- General configuration templating or substitution.

## 5 Implemented State

`Threadsmith.Tools` owns the provider-neutral `SecretReference`, request/result/failure/source-trust contracts, deterministic `SecretResolver`, environment provider, strict bounded user JSON provider, and confined Git-proven repository JSON provider. `Threadsmith.App` composes one host-owned provider set. Repository secret JSON and the normalized `THREADSMITH_secrets__...` environment subtree are excluded from ordinary `IConfiguration`, so only explicit typed consumers can materialize a value.

Model provider activation, Brave search, MCP stdio/static headers/OAuth client secrets, managed HTTP hooks, and private NuGet advisory sources use `ISecretResolver` at their final privileged boundary. Brave authentication requires `UserOwned` trust so repository values cannot supply or override its API key. Fixed precedence is environment → eligible repository → user. MCP and Codex access/refresh-token caches remain lifecycle-specific. The legacy `ISecretStore` exists only behind compatibility adapters for OAuth cache ownership and older tests.

Focused `Threadsmith.SecretResolution.Tests` coverage proves canonical parsing, deterministic precedence, minimum trust, real Git tracked/index rejection and effective-ignore success, duplicate JSON rejection, registration ambiguity, provider-neutral fake-provider integration and diagnostic sanitization, linked timeout cancellation, cancellable Git validation, host-aware repository identity, and canary-safe result rendering. The maintained MTP-227–230 procedures retain cross-platform owner-permission, real-consumer/OAuth, interruption/concurrency, and whole-system redaction closeout.

## 6 Proposed Design

### 6.1 References and resolver

Introduce a validated `SecretReference` value object with a canonical logical name beginning with `secrets:`. Parsing rejects empty segments, control characters, traversal syntax, ambiguous separators, excessive length, and case-insensitive canonical collisions.

`ISecretResolver.ResolveAsync` accepts a `SecretResolutionRequest` containing the reference, requesting component/capability ID, repository identity when applicable, required minimum provider trust, allowed provider IDs where policy narrows them, and a non-secret purpose classification. It returns a discriminated result rather than `null` or provider exceptions.

The resolver receives `IEnumerable<ISecretProvider>`, validates deterministic unique provider IDs and priority at composition, applies host policy, invokes eligible providers in order, and stops at the first successful value. Secret values use a narrow non-serializable host-owned handle/value contract, are held only as long as the owning transport operation needs them, and are never included in `ToString`, equality diagnostics, events, or projections.

### 6.2 Provider contract and extensibility

Each `ISecretProvider` declares stable ID, source kind, priority, trust classification, supported reference namespace, and availability. `TryResolveAsync` returns `Found`, `NotFound`, `Unavailable`, `Rejected`, or `Failed` plus a bounded safe diagnostic code. Providers propagate cancellation and may not throw raw provider errors across the resolver boundary.

Initial compiled order is:

1. environment variables — process/user automation override;
2. repository store — repository-specific value when permitted by request trust;
3. user store — durable user default.

The order is host-owned and documented. Repository configuration cannot reorder providers. A trusted user/machine policy may disable a provider or require a minimum source trust for selected components, but cannot cause secret values to enter ordinary configuration.

Future providers, including AWS Secrets Manager, implement the same contract and may own bounded networking, caching, authentication, and diagnostics behind their adapter. Provider bootstrap credentials must not resolve cyclically through the provider being initialized; workload identity or another already-available provider is required.

### 6.3 Secret-aware configuration

Add explicit typed metadata or descriptors marking individual configuration properties as secret-reference-aware. Binding validates and retains a `SecretReference`; it does not replace the property with its value. Resolution occurs only when the owning component performs the privileged operation.

Unknown/untyped configuration, repository JSON traversal, context inspection, status, diagnostics, model request assembly, and configuration dumping never trigger secret discovery. A plain string equal to `secrets:example` remains a string unless its schema declares secret awareness or a component explicitly parses and resolves it.

Startup validation may verify reference syntax and provider availability without fetching values. Eager secret retrieval is permitted only where activation cannot be determined safely otherwise and must still use the resolver with sanitized errors.

### 6.4 Repository provider

Use `<repository>/.threadsmith/secrets/config.json` as the canonical initial file, retaining compatible nested JSON names. Before reading it, the provider:

- confines the canonical path beneath the active repository and rejects symlink/reparse-point escapes;
- after Git validation, opens without following links and with Unix nonblocking semantics, validates from the same stable handle that the target is a regular file plus its resolved path and reparse attributes, and reads only through that handle so path replacement cannot redirect the read and a FIFO/device/socket cannot block resolution;
- applies strict UTF-8, duplicate-property, depth, key-count, and byte bounds;
- first proves from Git index state, with every pathspec scoped relative to the opened repository directory even when it is a worktree subdirectory, that neither the exact file nor an applicable tracked ancestor/store representation is tracked or staged for addition, and rejects before reading when any index entry could commit the secret content;
- only after the index-state rejection, proves the untracked file is covered by effective Git ignore rules using bounded non-mutating Git commands; `git check-ignore --no-index` may assist with rule evaluation but is never sufficient by itself because it deliberately disregards tracked state;
- rejects when Git is unavailable, the repository is not a usable Git worktree, tracked/staged state or ignore status is indeterminate, the file is tracked/staged, or the exact file is not effectively ignored;
- reports actionable instructions without reading or exposing values when either the index-state or ignore proof fails.

Repository secrets have `RepositoryOwned` trust and cannot satisfy requests requiring `UserOwned` or stronger sources. The repository cannot use its secret file to bootstrap trusted policy, outbound-consent authority, provider registration, or organization control.

### 6.5 User provider

Use `~/.threadsmith/secrets/config.json`, created only through a host-owned secret-management boundary or explicit user action. Apply strict UTF-8, duplicate/depth/count/size bounds, atomic replacement, and owner-only permission checks appropriate to each OS. Unix rejects group/other permission bits. Windows requires current-user ownership and rejects allow ACL entries granting filesystem authority to principals other than the current user, Local System, built-in Administrators, or owner placeholders; it repeats the check from the opened handle. Unsafe or indeterminate permissions fail closed.

Values are never printed by list/status operations. Future `/secrets` management UX may list logical names and provider/source status, set values through masked input, and remove values atomically; implementation may include that surface if required for a usable exit criterion, but no command accepts a secret value as a normal command-line argument.

### 6.6 Environment provider

Preserve `THREADSMITH_` mapping with double underscores as configuration separators. For `secrets:BRAVE_SEARCH_API_KEY`, resolve `THREADSMITH_secrets__BRAVE_SEARCH_API_KEY`. Environment variables remain suitable for CI, containers, one-process overrides, and users who prefer them.

The provider reads only the exact derived name, never enumerates or projects the environment, and distinguishes absent from empty/rejected values. Ordinary prefixed environment configuration separately filters the complete normalized `secrets` subtree before exposing configuration data. Documentation presents environment variables as an option, not the primary interactive-user workflow.

### 6.7 Failures and user guidance

Aggregate failure distinguishes malformed reference, no eligible provider, not found, provider unavailable, repository store tracked/staged or not ignored, unsafe path/permissions, access denied, malformed store, timeout/cancellation, bootstrap cycle, and policy/trust rejection.

User-facing messages identify the logical reference, requesting component, providers attempted/skipped, safe reason codes, and exact remediation commands/paths where appropriate. They never include secret values, raw provider exceptions, environment contents, file contents, remote response bodies, OAuth data, or sensitive path/query material. Headless output uses stable machine-readable classifications and exit behavior.

### 6.8 Migration and compatibility

Replace `ConfigurationSecretStore` consumption incrementally behind a compatibility adapter, then remove component-specific static lookup after all call sites migrate. Existing logical `secrets:` names remain stable. Existing repository secret JSON remains compatible after ignore proof succeeds. Existing environment variables retain their names and gain deterministic precedence.

OAuth stores remain lifecycle-specific consumers/storage owners in this plan. Their bootstrap client secrets and other static references use `ISecretResolver`; access/refresh-token persistence stays behind the OAuth token-store contracts until a future plan intentionally unifies writable credential storage.

## 7 Public Contracts

Add host-owned contracts equivalent to:

- `SecretReference`;
- `SecretResolutionRequest` and `SecretResolutionContext`;
- `SecretResolutionResult` and closed failure classifications;
- `ISecretResolver`;
- `ISecretProvider` and `SecretProviderResult`;
- provider metadata/trust/source-kind contracts;
- typed secret-aware configuration descriptors.

Do not expose `IConfiguration`, JSON nodes, environment collections, filesystem handles, Git process types, cloud SDK types, OAuth token types, or raw secret strings in public projections. Keep external provider SDKs behind implementation adapters.

## 8 Project/File Changes

- `Threadsmith.Core` or the smallest existing host-contract owner — reference/result/context DTOs without storage dependencies.
- `Threadsmith.Tools` or a dedicated existing configuration/security layer selected after inspection — resolver and initial providers.
- `Threadsmith.App` — deterministic provider registration, typed configuration integration, and compatibility migration.
- Static-secret consumers across Models, Tools, MCP, Hooks, Validation, and provider adapters — resolve through the common boundary.
- TUI/CLI — masked management and actionable failure projection if included.
- Focused unit, integration, architecture, configuration, and security tests.
- ADR-48, Scenario AB, plan/milestone indexes, DAG/shared context, documentation, and DOX.

Do not create a new product project unless dependency-direction inspection proves existing layers cannot own the contracts cleanly. Any new project-level file copied to output uses `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Inventory every secret reference, `ISecretStore` call site, trusted/full configuration boundary, OAuth token store, configuration descriptor, redaction path, and current user/repository secret format.
2. Add ADR-48 defining resolution authority, provider trust/order, secret-aware configuration, repository-ignore proof, and OAuth compatibility.
3. Define immutable secret reference/request/result/provider contracts and sanitized failure taxonomy.
4. Implement the aggregate resolver with deterministic registration, policy/trust filtering, cancellation, duplicate detection, and no-value observability.
5. Implement bounded environment, repository JSON, and user JSON providers.
6. Add tracked/staged Git-index rejection and effective Git-ignore verification plus confinement, stable-handle no-follow reading, reparse-point, encoding, size, duplicate, and permission defenses.
7. Add typed secret-aware configuration metadata and prove ordinary strings/config inspection cannot trigger resolution.
8. Migrate static-secret consumers and preserve existing logical names and environment compatibility.
9. Add masked user management if required to make the user store operable without hand-editing; never accept values in normal command arguments.
10. Integrate actionable interactive/headless failures, redaction, diagnostics, hooks, and support bundles.
11. Add deterministic provider-order, trust, malformed-store, ignore, cancellation, migration, concurrency, and architecture tests.
12. Update Scenario AB, documentation, status/index/DAG/shared context, and DOX; update the user guide/manual plan only when implementation ships.

## 10 Testing

Automated coverage must verify:

- each initial provider resolves the same logical reference through one resolver;
- deterministic precedence and host-owned provider order;
- repository configuration cannot reorder/register providers or weaken minimum trust;
- repository secrets are rejected before value reads when the file is tracked/staged, tracked-state proof is indeterminate, or exact effective Git-ignore proof fails or is indeterminate;
- untracked, effectively ignored repository secrets resolve only for requests permitting repository-owned sources;
- confinement rejects traversal, symlink/reparse escapes including replacement between Git validation and open, Unix non-regular files without blocking, malformed JSON, duplicate keys, unsafe encoding, oversize, and excessive depth/count;
- user-store atomicity and platform permission behavior;
- environment mapping compatibility without enumeration by the secret provider, plus absence of the normalized secret subtree from ordinary and trusted `IConfiguration` roots;
- secret-aware typed fields resolve at the final boundary while arbitrary strings and configuration inspection never do;
- explicit component resolution works with the same policy and errors;
- not-found/unavailable/rejected/timeout/cancel/bootstrap-cycle failures are actionable and secret-free;
- concurrent requests do not leak, corrupt stores, reorder precedence, or duplicate unsafe provider work;
- static model, Brave, MCP, hook, NuGet/private-source, and other migrated credentials work through the resolver;
- OAuth lifecycle/token-cache behavior remains compatible while static OAuth bootstrap references use the resolver;
- logs, events, telemetry, context, model requests, diagnostics, support bundles, exceptions, and terminal output contain no canary secret;
- dependency-direction and extension/skill authority tests prevent untrusted provider registration or store inspection.

## 11 Security/Permissions

Rejecting tracked/staged index state and then proving effective Git-ignore coverage prevents the planned provider from accepting a file Git can continue committing, but does not encrypt repository secrets or make repository content trusted. User documentation must state this plainly. Repository values cannot satisfy higher-trust requests. Secret provider selection and source-trust requirements are host-owned.

Providers return values only to the resolver, which returns them only to an authorized component boundary. Secret values never become configuration values, durable DTOs, events, logs, model evidence, hook payloads, or tool results. Zeroization is best-effort where managed strings or third-party SDKs make guarantees impossible; minimize lifetime and copies rather than claiming impossible erasure.

Future network providers must add endpoint policy, bounded transport, authentication bootstrap, retries, cancellation, audit, and SDK isolation without weakening this contract.

## 12 Observability

Record bounded provider ID, source kind, requesting component, reference digest or safe logical identifier according to redaction policy, outcome classification, and duration. Never record values, store content, environment content, remote bodies, raw provider exceptions, or OAuth tokens.

Diagnostics should answer why discovery failed and how to fix it. Successful resolution should normally remain quiet except for coarse metrics and audit needed to establish source/trust behavior.

## 13 Migration/Compatibility

Existing `secrets:` references and `THREADSMITH_` environment names remain valid. Existing `.threadsmith/secrets/config.json` becomes subject to mandatory effective ignore proof. This is an intentional fail-closed compatibility change and requires clear remediation.

User-level storage is additive. Machine/user ordinary configuration containing inline values remains readable only during a bounded migration window if current behavior requires it; implementation must choose and document either explicit rejection or a deprecation path, never silently copy values.

OAuth caches are not migrated by this plan. No durable database migration is expected unless implementation inspection proves provider metadata must be persisted; secret values must never enter SQLite.

## 14 Acceptance Criteria

- All migrated static-secret consumers use one `ISecretResolver` and no longer choose configuration layers themselves.
- Users can store any supported static secret in the user store, repository store when proven untracked, effectively ignored, and trust-eligible, or environment variables.
- Typed secret-aware configuration and explicit component calls are the only discovery triggers.
- Repository secrets fail with actionable guidance unless Git index state proves the exact canonical store and every applicable ancestor/store representation untracked and effective Git rules prove the file ignored.
- Resolution order is deterministic, documented, host-owned, and tested; environment variables remain optional and compatible.
- Missing, malformed, unavailable, unsafe, and policy-rejected secrets produce helpful sanitized interactive and headless errors.
- Future providers can implement `ISecretProvider` without modifying consuming components or leaking provider SDK types.
- Secret values remain absent from configuration projections, model context, logs, events, persistence, diagnostics, support bundles, and tests.
- ADR-48, Scenario AB, focused tests, documentation, milestone/index/DAG/shared-context updates, and DOX pass before M22.2 is marked complete.

## 15 Risks

- **Generic interpolation leaks values:** only typed secret-aware fields or explicit component requests trigger resolution; results never rewrite configuration.
- **Repository secret committed accidentally:** reject tracked/staged index state before evaluating effective ignore coverage, fail closed when either proof is indeterminate, and state clearly that ignore is not encryption.
- **Precedence surprises:** fixed documented provider order, source reporting without values, and collision tests.
- **Lower-trust source overrides privileged credentials:** request-level minimum trust filters providers before lookup.
- **Provider extension becomes code-execution/trust bypass:** initial registration is compiled and host-owned; future external registration requires a separate trusted contract.
- **Cloud-provider bootstrap cycle:** detect/reject cycles and require workload identity or an independent provider.
- **Managed-memory retention:** minimize value lifetime/copies and avoid unsupported zeroization claims.
- **OAuth regression:** retain lifecycle-specific token stores and migrate only their static bootstrap references.

## 16 Documentation

Implementation updates Plan 62, M22.2 detail/index/DAG/shared-context status, Scenario AB, ADR-48, the user guide, configuration example, model/MCP/hook/static-secret operations, maintained manual plan, and the DOX chain. Maintained limitations are identified explicitly rather than presenting them as completed verification.

## 17 Open Decisions

Resolved for planning:

- One resolver serves all components and secret-aware configuration.
- Arbitrary configuration strings are never implicitly resolved.
- Initial providers are environment, repository JSON, and user JSON with deterministic host-owned precedence.
- Environment variables remain supported but are not the primary user workflow.
- Repository stores require tracked/staged index-state rejection plus effective Git-ignore proof and remain lower trust.
- Provider extensibility is interface-based; consuming components remain provider-neutral.
- OAuth lifecycle caches remain specialized in this milestone.

Implementation resolutions:

- `Threadsmith.Tools` owns provider-neutral contracts and initial local providers; `Threadsmith.App` owns compiled registration.
- No `/secrets` command ships initially. Users explicitly manage the bounded user JSON store; no ordinary command argument accepts a secret value.
- Unix user stores fail closed when group/other permission bits are present. Windows relies on the existing user-profile ACL boundary and documents that users must not widen it; maintained cross-platform checks remain.
- Inline static values in ordinary configuration are not migrated or resolved. Existing logical references/environment names remain compatible; the repository secret file is intentionally removed from general configuration and becomes resolver-only.
