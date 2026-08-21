# AGENTS.md — Threadsmith.Mcp

## Purpose

Own MCP profile loading, SDK-isolated transports, capability translation, lifecycle management, OAuth identity operations, and imported-tool adaptation.

## Ownership

- This project is the only product layer that references the official MCP SDK.
- `McpManager` is the single lifecycle authority composed by `Threadsmith.App`; Core owns only provider-neutral command/result DTOs.
- `McpAdapter` owns live transport generations and registry publication; `ToolStateManager` remains the repository availability owner.

## Local Contracts

- Effective profiles come only from repository-excluding trusted configuration. Profile fields, timeouts, collections, executable/HTTPS eligibility, OAuth mode, and capability kinds fail closed under host bounds.
- SDK types, live handles, process identifiers, tokens, headers, callback data, and raw server objects never cross into Core DTOs or durable state.
- Connect/disconnect/auth transitions serialize per profile and use generation fencing. Invocation admission closes atomically before registry removal and bounded in-flight drain; every imported tool lease is also bound to the exact capability generation so a pre-resolved stale proxy cannot execute after schema replacement. Automatic startup/rebind attempts may use cached OAuth but must disable user authorization callbacks; explicit connect/authentication alone may launch browser or pasted-callback UX. Stdio uses a host-owned child process around the SDK stream transport so newline frames are bounded before JSON parsing, stderr is bounded before sanitization, graceful stdin closure and process-tree kill share one deadline, and startup failure cleanup starts even after the caller deadline. HTTP shutdown independently starts every owned subscription/client/transport cleanup within that deadline and releases its owned `HttpClient` even after timeout.
- Imported tools start disabled and are classified as opaque executable behavior, never as read-only from profile trust or server annotations. Exact user-owned enablement makes an imported tool eligible for conversational advertisement, but opaque tools retain at least the repository's executable-trust floor and ordinary runtime trust, allow/deny, network, approval, and invocation policy remain authoritative. Enablement is atomically fenced to the exact reviewed tool version so a list-change cannot approve a replacement schema. Persisted availability is bound to repository plus profile-qualified capability identity and normalized metadata/schema digest in a user-owned approval store outside repository control; repository settings may narrow but cannot grant approval.
- Tools, resources, resource templates, prompts, schemas, arguments, returned content, and failure messages are sanitized and bounded. HTTP response bodies and stdio frames are bounded before SDK materialization; SSE resets its bound only at event boundaries. The aggregate 256-capability generation bound is enforced before imported-tool construction or registry publication. Advertised list-change notifications feed one serialized dirty/debounce loop into a complete replacement snapshot; notifications before publication are retained and notifications during discovery request one follow-up pass. Registry ownership, invocation leases, and manager generations fail stale schemas closed. Explicit resource/prompt reads use a policy-only host action through the central tool pipeline before transport access, including repository trust, network/secret policy, managed hooks, approval, and invocation audit. Resource/prompt content is explicit untrusted output, redacted before projection, preserves aggregate truncation when content items are omitted, and is never automatically advertised or inserted into model context.
- Stdio status reports transport-owned live-process presence even when the SDK does not expose a safe process identifier; absence of a PID must not imply absence of the connected child process.
- SDK-backed transports pin the broadly deployed MCP `2025-06-18` protocol rather than opting into a newer discovery handshake by package default. Owned HTTP clients send a bounded product `User-Agent`. OAuth metadata compatibility permits one bounded HTTPS proxy-document hop to its HTTPS canonical issuer, preserves that validated document's endpoints, rejects insecure/malformed/multi-hop metadata, and requires no server-specific profile fields.
- OAuth cache mutation is exact-profile scoped and atomically replaces one coherent token/client grant while pruning superseded credentials; readers consume one immutable profile snapshot so concurrent replacement cannot tear grant fields. Field-level compatibility credentials migrate only after two identical complete reads and never fill a partial local grant. OAuth profiles use one closed pre-registered, Client ID Metadata Document, or dynamic-registration mode; optional strings normalize before mode selection. URL-only dynamic registration occurs only during explicit interaction-authorized operations; automatic attempts require a cached issuer-bound client and cannot create a remote registration. Interactive `localhost` callbacks reserve both IPv4 and IPv6 loopback listeners and bound callback input. The live HTTP transport owns an unused reservation through its full lifetime, releases it on failed startup or disconnect, and reacquires the exact registered URI when a later authorization follows a consumed callback; cached dynamic-registration client credentials are reused only when their redirect URI exactly matches the current callback URI. Configured client secrets require a client id, and returned client credentials remain cache-only secret material. Logout persists prefix tombstones so credentials found through the compatibility secret-store fallback cannot be resolved again after local removal. Logout makes no remote claim; revocation reuses the bounded metadata-compatibility authority, requires a same-origin HTTPS RFC 7009 endpoint and the grant-bound client authentication method, and treats a configured client id/secret reference as current authority over cached credential values so secret rotation takes effect. Network failures and host-owned timeouts are unconfirmed remote outcomes; explicit local-cleanup authorization must still clear the exact profile cache.
- Diagnostics use protocol ping only, expose coarse sanitized state and monotonic timings, and never invoke an arbitrary tool or reveal credentials/content.

## Work Guidance

- Inspect official MCP C# SDK source/docs before changing SDK calls or protocol mapping.
- Preserve the overall drain/kill deadline when dividing drain and transport-stop windows.
- Add new user operations through Core contracts and `IMcpManager`; do not add terminal or CLI dependencies here.
- Keep static-secret resolution on `ISecretResolver`; lifecycle-owned OAuth cache remains the specialized exception.

## Verification

- `dotnet build src\Threadsmith.sln --no-restore`
- `tests\Threadsmith.Milestone8.Tests\bin\Debug\net10.0\Threadsmith.Milestone8.Tests.exe`
- `tests\Threadsmith.Milestone9.Tests\bin\Debug\net10.0\Threadsmith.Milestone9.Tests.exe`
- `tests\Threadsmith.Milestone10.Tests\bin\Debug\net10.0\Threadsmith.Milestone10.Tests.exe`
- `tests\Threadsmith.Milestone23.Tests\bin\Debug\net10.0\Threadsmith.Milestone23.Tests.exe`
- `tests\Threadsmith.Architecture.Tests\bin\Debug\net10.0\Threadsmith.Architecture.Tests.exe`

## Child DOX Index
