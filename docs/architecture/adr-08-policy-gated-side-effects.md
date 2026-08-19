# ADR-08: Policy-Gated Side Effects

## Decision

Every user or UI action enters the host as an application command. Side-effecting handlers accept a `CancellationToken` and consult the host-owned `IApprovalPolicy` contract when approval is required. A model output never grants its own approval.

## Consequences

- TUI and headless execution share command handlers.
- Cancellation and future permission enforcement have one application boundary.
- Tool and mutation plans can extend policy without changing UI contracts.
