# ADR-49: Canonical Release-License Closure and Fail-Closed Publication

- **Status:** Accepted
- **Date:** 2026-08-16
- **Deciders:** Threadsmith.NET maintainers and designated release owner

## Context

Plan 45 produces self-contained application and worker payloads for six RIDs and
already stages hash-pinned ripgrep attribution. The public-release inventory found
that the remaining NuGet and RID runtime closure lacks a canonical generated notice
bundle, SBOM, RID runtime legal files, and a recorded Windows self-contained-runtime
distribution decision. Hand-maintained notices cannot safely track transitive or
runtime changes.

## Decision

M23.2 will make release legal material a canonical host-owned release artifact:

- derive a bounded resolved closure from the exact publish inputs, not repository
  configuration or untrusted package content;
- maintain reviewed component evidence and license dispositions for every bundled
  component, including source/notice provenance where required;
- generate both human-readable attribution material and an SPDX or CycloneDX SBOM;
- stage exact RID runtime `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` files with
  version/digest provenance;
- require a designated release-owner decision record for the selected Windows
  self-contained distribution terms; and
- fail packaging/publication when required evidence or legal artifacts are absent,
  stale, malformed, or do not match the staged payload.

The existing self-contained model and ripgrep contract remain. Framework-dependent
deployment is not selected by this ADR.

## Consequences

- Release work gains deterministic legal artifacts and six-RID verification gates.
- Adding/updating a bundled package, runtime, native binary, installer, or release
  tool requires a reviewed inventory disposition.
- Legal approval remains a human authority: automation verifies an approved record
  and artifact consistency but cannot infer legal permission.
- SBOM generation does not replace license, NOTICE, or MPL source-availability
  obligations.
