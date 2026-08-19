# ADR-39: Adapt compatible skill directories and centralize active model selection

## Status

Accepted (2026-08-10).

## Context

Threadsmith-native Plan-39 packages provide signed or exact-digest manifests and closed workflows, while portable Agent Skills and Claude Code commonly use an unsigned `SKILL.md` directory. Model provider catalogs are immutable, but repository-local runtime selection requires a mutable session authority without changing an in-flight request.

## Decision

- Treat Claude-style skills as untrusted compatibility sources, never as native signed packages.
- Pin the accepted portable and Claude-extension metadata in `docs/skill-compatibility-spec-v1.md`.
- Discover only bounded frontmatter. Full instructions and confined strict-UTF-8 resources are loaded on explicit activation and assigned a canonical path/length/raw-byte SHA-256 digest.
- Reject links, path escapes, unsafe YAML features, executable auto-activation, and unsupported hook/agent/fork semantics. Tool names map only through a closed semantic table and remain subject to the central tool pipeline.
- Keep compatibility status explicit: `Compatible`, `CompatibleWithRestrictions`, or `Unsupported`.
- Use `ActiveModelSelectionService` as the session authority for configured provider/profile/reasoning state. New requests capture the current profile generation; existing requests retain their prior snapshot.
- Persist repository selection as stable provider/profile ids plus a host reasoning level in `.threadsmith/config.json`. Present-invalid repository intent fails closed instead of falling back to user defaults.
- Runtime provider dispatch resolves the current profile for each new request. `/models` and `/reasoning` use the same authority and atomic writer.

## Consequences

Portable skills can be inspected without repackaging, but their source format never grants trust, tools, scripts, network, mutation, or delegation authority. Repository model choice is durable and changes actual routing rather than only terminal labels. A switch invalidates stale context occupancy while cumulative provider usage remains historical.
