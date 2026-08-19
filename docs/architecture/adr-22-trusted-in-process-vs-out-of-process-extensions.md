# ADR-22: Trusted In-Process versus Future Out-of-Process Extensions

Status: Accepted
Date: 2026-08-04
Strategy: §17.24, §22.2, §36, §29 (item 15)

## Context

Extensions are loaded into the host process via collectible `AssemblyLoadContext`s (ADR-20). An ALC
is **not a security boundary**: a malicious extension loaded in-process can call any API the host
process can call, read secrets, and exhaust resources. The product must nevertheless support
extensions without assuming they are fully trusted.

## Decision

The M7 extension runtime treats extensions as **trusted in-process** components and applies
defense-in-depth resource and capability controls, not a hard security boundary.

- Each extension declares required permissions in its manifest (`repository-read`,
  `repository-write`, `process-execute`, `network-access`, `secret-reference`); the host grants only
  what the repository trust level permits (`RepositoryTrustLevel`) and rejects incompatible
  extensions (`ExtensionIncompatibleException`).
- Per-extension invocation budgets (`ExtensionInvocationBudget`, §22.2) bound the number of
  invocations per turn; leases (ADR-24) bound in-flight execution.
- Capability contracts return host-owned DTOs only; no extension type crosses the boundary (§7.1).
- Untrusted extensions (`UntrustedInspection`) are refused at load; a future out-of-process extension
  host (separate process, IPC) is the path to hard isolation and is deliberately deferred.

## Consequences

- Repository trust is the policy gate today; the ALC provides isolation/unload, not security.
- The design does not yet defend against a hostile trusted extension; that is accepted until the
  out-of-process host exists.
- The capability/lease/budget contracts are structured so an out-of-process adapter can replace the
  in-process proxy without changing the tool pipeline (§8.1).

## Alternatives considered

- Treating the ALC as a security boundary: rejected — false sense of safety (§17.24, §36).
- Building the out-of-process host now: rejected — out of M7 scope; the in-process contracts are
  shaped to allow it later.