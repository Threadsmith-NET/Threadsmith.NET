# Lifecycle hooks

Lifecycle hooks notify or consult configured automation at stable host boundaries. They do not replace Threadsmith's event stream, tools, plans, mutation policy, validation, extensions, MCP, skills, or execution state machine.

## Safety model

- Hooks are advisory and fail-open by default.
- Repository declarations start disabled and require explicit external approval of the exact repository identity and configuration digest. They remain advisory/fail-open after approval.
- Only repository-excluding managed organization, machine, or user policy can grant blocking/fail-closed authority, and only for eligible `Before*`, `PlanProposed`, or `MutationStaged` points and named denial codes.
- A hook result never approves an operation, changes inputs/results, supplies commands or patches, grants trust/tools/secrets, or skips validation.
- Executables and extensions run with host-process permissions. Timeouts and unload contexts are not security sandboxes. HTTP and MCP disclose the granted envelope to an external system.

## Handler types

- **Executable:** a bare executable name resolved from the curated host `PATH`; versioned JSON enters standard input and one bounded JSON result leaves standard output. No shell interpolation or command-line secret is used.
- **HTTP:** HTTPS by default (literal loopback HTTP is allowed for development), one bounded POST/response, no redirect following, and at most one explicitly scoped bearer secret reference. Managed hook credentials resolve only from user-owned sources through the common static-secret boundary; repository values cannot bootstrap trusted hook authority.
- **MCP:** an already-connected immutable profile/tool identity invoked through the existing governed adapter. Hook-originated tool hooks must be suppressed by the invoker.
- **Extension:** an active immutable extension generation/capability invoked under its existing lease and budget.

## Results

Handlers return exactly one schema-1 result: `acknowledge`, `advice`, `deny`, or `failure`. Findings and explanations are bounded untrusted text. Unknown versions/types, invalid fields, excessive output, timeout, cancellation, unavailable adapters, and budget exhaustion are normalized failures.

## Repository approval and revocation

Inspect the normalized handler identity, target, points, limits, requested data/secret names, digest, and advisory authority before approving. Approval is stored in the user-owned database, not `.threadsmith`. Any identity, target, point, limit, data/secret request, or digest change requires fresh approval. Revocation takes effect on the next policy resolution.

Interactive management uses `/hooks list`, `/hooks inspect <id>`, `/hooks enable|disable <id>`, `/hooks test <id>`, `/hooks approve|revoke <id>`, and `/hooks audit [id]`. Approval and revocation require an open repository. Headless automation uses the corresponding `HeadlessShell` methods over the same command dispatcher. Testing is targeted to the requested handler and never invokes its peers.

## Audit and recovery

Audit records show eligibility, immutable identity/digest, operation, point, status, effective authority/failure mode, granted data/secret names, normalized result code, duration, and host decision. They never contain secret values or raw prompts, arguments, diffs, files, provider output, or validation logs.

Interrupted hook invocations are never blindly replayed. Re-invocation is legal only for an explicitly idempotent pre-action handler when no durable owning outcome exists; otherwise obtain a fresh decision. Post-action notifications may be recorded as missed and retried only as separately audited notifications. A handler that ignores cancellation is abandoned when its timeout expires; any late result is discarded.
