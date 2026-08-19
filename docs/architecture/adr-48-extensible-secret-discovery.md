# ADR-48: Static secrets resolve through one host-owned provider boundary

**Status:** Accepted

## Context

Threadsmith configurations already retained logical `secrets:` references, but static values were obtained from an effective `IConfiguration` view. That made source trust and precedence depend on which configuration view a component received, allowed repository secret JSON to enter the ordinary configuration graph, and gave model, web, MCP, hook, and NuGet consumers inconsistent failures. OAuth access and refresh tokens have separate lifecycle, refresh, and removal semantics and must not become generic static configuration.

## Decision

Threadsmith uses `ISecretResolver` as the only ordinary static-secret consumer boundary. A caller submits a validated `SecretReference`, component ID, non-secret purpose, optional repository identity/provider allowlist, and minimum source trust. The resolver applies fixed compiled provider order, trust filtering, cancellation, namespace support, duplicate-registration rejection, and bootstrap-cycle detection. Results expose only a narrow redacting `SecretValue`, source ID, and closed safe diagnostics. Provider exceptions do not cross the boundary.

The initial compiled providers and precedence are:

1. the exact derived `THREADSMITH_` environment variable;
2. `<repository>/.threadsmith/secrets/config.json` when repository trust is eligible;
3. `~/.threadsmith/secrets/config.json`.

Repository configuration cannot register, reorder, or weaken providers. Repository values are `RepositoryOwned` and cannot satisfy `UserOwned` or managed requests. The repository provider confines the exact path, rejects reparse points, proves that neither the file nor any applicable ancestor/store representation exists in the Git index, and separately proves effective ignore coverage before opening. Index pathspecs are relative to the opened repository directory rather than the enclosing worktree root, so opening a worktree subdirectory does not move the safety probe away from its store. It then opens the file without following links and, on Unix, with nonblocking semantics; validates from that same stable handle that the target is a regular file plus the expected resolved path and reparse state; and reads only through the validated handle. Replacement races therefore cannot redirect the read, and repository-controlled FIFOs/devices/sockets cannot block resolution. Git absence or indeterminate index/ignore state fails closed. Ignore is accident prevention, not encryption or an elevation of repository trust.

Both JSON providers use strict UTF-8 and bounded size, depth, and property counts; empty/non-string values and case-insensitive duplicate properties fail closed. The user store requires owner-only Unix permissions. On Windows, the file must be owned by the current user and allow filesystem authority only to that user, Local System, built-in Administrators, or owner placeholders; the check is repeated from the opened handle so replacement cannot bypass it. Unsafe or indeterminate ACLs fail closed. Environment lookup derives and reads one exact variable and never enumerates the environment. The ordinary and repository-excluding configuration roots use a filtered `THREADSMITH_` provider that excludes the complete normalized `secrets` subtree, so compatible environment values never become generally accessible `IConfiguration` entries.

Typed configuration retains logical references. It never substitutes values into `IConfiguration`, model context, inspection, events, persistence, hooks, diagnostics, or support bundles. Resolution occurs at the final model transport, Brave request, MCP bootstrap/header/process, managed hook, or NuGet process boundary. A normal string resembling `secrets:` has no effect unless a typed consumer explicitly parses and requests it.

`ISecretProvider` is a provider-neutral compiled extension point with ID, priority, trust, source kind, supported prefixes, and a closed result. Future network providers must preserve host registration authority, SDK isolation, endpoint policy, bounded transport, cancellation, safe diagnostics, and non-cyclic bootstrap. Installing a skill, extension, hook, or MCP server does not grant provider-registration authority.

The legacy `ISecretStore` remains only as a narrow compatibility adapter for lifecycle-owned OAuth token stores and older tests while static consumers migrate. MCP and Codex access/refresh-token caches retain their specialized owners. Static MCP OAuth client secrets use the common resolver; lifecycle tokens never enter either generic JSON store.

## Consequences

Users may choose durable user storage, a lower-trust repository-local ignored store, or environment variables without changing consumers. Existing logical names and `THREADSMITH_` mappings remain compatible. A previously tracked repository secret store now fails intentionally and must be removed from the index and effectively ignored before use. Failures identify only the logical reference, component, safe provider outcomes, and remediation. The initial user store is explicitly managed by the user; no command accepts secret values as ordinary CLI arguments or shell-history-bearing text.
