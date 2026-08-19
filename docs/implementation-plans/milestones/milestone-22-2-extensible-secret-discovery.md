## Milestone 22.2 - Extensible Secret Discovery  *(plan 62)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Give every secret-aware configuration field and explicitly authorized component one consistent host-owned resolution boundary backed by extensible providers, with safe repository/user storage, optional environment-variable compatibility, and actionable secret-free failures.

**Deliverables:**
- Provider-neutral `ISecretResolver`, `ISecretProvider`, validated reference/request/result contracts, source trust, and sanitized failure taxonomy.
- Typed secret-aware configuration fields that retain logical references and resolve only at the final privileged component boundary.
- Explicit component resolution through the same resolver, policy, trust, precedence, and diagnostics.
- Repository storage at `<repository>/.threadsmith/secrets/config.json`, accepted only after confinement, tracked/staged Git-index rejection, and effective Git-ignore proof.
- User storage at `~/.threadsmith/secrets/config.json` with bounded parsing, atomic writes where managed, and platform-appropriate owner-permission handling.
- Optional `THREADSMITH_` environment-variable provider preserving existing names and `__` separator mapping.
- Deterministic host-owned provider order and minimum-source-trust enforcement that repository configuration cannot weaken.
- Migration of static model, web, MCP, hook, private-source, and other configuration-backed credentials without moving lifecycle-owned OAuth token caches.
- Extensibility contract suitable for future AWS Secrets Manager and similar providers without changing consumers.
- ADR-48, Scenario AB, focused security/configuration/integration tests, documentation, and DOX.

**Exit criteria:**
- Migrated static-secret consumers depend on one resolver rather than selecting configuration layers or provider implementations.
- A logical secret can be supplied from the user store, a proven-untracked, effectively ignored, and trust-eligible repository store, or an environment variable with deterministic documented precedence.
- Only typed secret-aware fields and explicit component requests trigger resolution; ordinary configuration strings and inspection never materialize values.
- Repository lookup fails before reading values unless Git index state proves the exact confined store untracked and effective Git rules prove it ignored, with actionable remediation.
- Missing, malformed, unavailable, unsafe, timed-out, cyclic, and trust/policy-rejected resolution returns stable helpful errors without values or raw provider failures.
- Repository configuration cannot register/reorder providers, widen source trust, or use repository secrets for user/managed authority.
- Secret values are absent from model context, projections, logs, events, persistence, hooks, diagnostics, support bundles, and terminal output.
- A future provider can implement the provider contract without changes to consuming components or leakage of external SDK types.
- OAuth lifecycle caches remain compatible; their static bootstrap references use the common resolver where applicable.
- Focused automated coverage, ADR-48, Scenario AB, docs, status, and DOX pass.

**Prerequisites:** plans 01, 07-08, 18-20, 23, 27, 31-32, 36, 39-40, 45, 50, 58, and 61.

**Scope decisions:**
- Prefer one user-facing and code-facing secret-discovery model while retaining environment variables as an option.
- Keep references separate from values; do not implement general configuration interpolation.
- Initial provider registration and order are compiled and host-owned; external provider registration requires a future trusted contract.
- Tracked/staged Git-index rejection and effective Git-ignore proof are both mandatory but are not represented as encryption or trust.
- Repository sources are lower trust and cannot satisfy user-owned or managed-policy requests.
- OAuth token persistence remains lifecycle-specific rather than being forced into a generic static-secret file.
- Future network providers must preserve cancellation, bounded transport, bootstrap-cycle prevention, redaction, and SDK isolation.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
