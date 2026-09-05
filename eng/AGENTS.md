# AGENTS.md — eng/

## Purpose

Own repository-maintained build, development-tool staging, and release automation that is not part of the Threadsmith runtime.

## Ownership

- `release/` — release target matrix; reviewed license evidence, deterministic SPDX/notices, exact runtime legal staging, artifact compliance; self-contained publish/staging; complete prompt-catalog validation; Windows, Linux, and macOS packaging; signing/notarization hooks; aggregate provenance; and release contract/smoke checks.
- `Stage-DevelopmentRipgrep.ps1` — detects or accepts one supported RID and delegates to the release-owned checksum-verified ripgrep stager under ignored `artifacts/dev-tools/<rid>` for source-development App builds.

## Local Contracts

- Release scripts accept immutable explicit versions and declared RIDs; unsupported targets fail before publish.
- The canonical staged payload is assembled from explicit same-RID application and scripting-worker publishes plus the matching pinned official ripgrep asset, reviewed exact package closure, generated notices/SPDX, and legal files from the exact restored RID/version runtime pack; runtime packs are validated separately from bundled NuGet packages. Each publish restores its requested RID and staging requires both self-contained apphosts, `tools/rg(.exe)`, and ripgrep license/provenance notices.
- Every application publish and staged or extracted release payload must contain exactly the code-declared flat `prompts/` catalog, with case-insensitive uniqueness and byte-identical source-to-publish assets. Packaging consumes the application publish and never reconstructs prompt files independently.
- Platform builders preserve absolute output roots exactly; repository-relative defaults resolve from the repository root.
- Native staged-payload execution occurs only when the host OS and architecture match the target; cross-published payloads receive structural validation without execution.
- Reviewed evidence and full legal inputs are tracked; generated payloads, notices, SBOMs, manifests, checksums, installers, and credentials remain outside Git history.
- Logs and manifests contain no secrets or machine-user paths.
- Trimming, Native AOT, and single-file assumptions remain disabled until separately proven compatible.
- Ordinary builds remain offline and do not download native tools. When the verified development stage exists, `Threadsmith.App.csproj` copies `tools/rg(.exe)` plus ripgrep license/source evidence into build and publish outputs.

## Work Guidance

- Keep common publish/staging logic host-neutral and isolate platform-native installer tooling.
- Development ripgrep staging remains explicit and delegates checksum, archive-root, executable, and legal-file validation to `release/Stage-Ripgrep.ps1`; do not duplicate or weaken that validation.
- Windows uses stable Inno Setup application identity; macOS uses stable package identifier `net.threadsmith.cli`; Linux replacement/deletion requires the exact installer ownership marker, a new install may use only an absent or empty unowned prefix, and unrelated launchers are never replaced.
- Release publication occurs only in the aggregate tag-gated CI job after all six exact artifacts and their digest-bound compliance sidecars pass verification. A sidecar may pass only after extraction proves every finalized staged file is present byte-for-byte in the archive or installer; the stage digest explicitly excludes its self-referential compliance record. Repository configuration cannot supply or override legal approval.
- Fail closed on dirty output directories, missing required payload files, version mismatches, unsupported RIDs, partial artifact sets, failed signature/notarization checks, non-official ripgrep metadata, archive digest mismatch, unknown/stale/unapproved legal evidence, missing MPL/SQLite attribution, missing or mismatched RID runtime legal files, missing third-party notices/SBOM/compliance bindings, or failed matching-architecture ripgrep smoke execution.

## Verification

- `pwsh -File eng/release/Test-ReleaseContracts.ps1`
- `pwsh -File eng/release/Publish-Release.ps1 -Version 0.1.0 -RuntimeIdentifier <host-rid> -OutputRoot .inbox/release-smoke`
- Run the matching platform builder for native package verification.
- After development-ripgrep staging changes, parse `eng/Stage-DevelopmentRipgrep.ps1`, stage one supported RID through the pinned manifest, build `Threadsmith.App`, and verify app-local `tools/rg(.exe)` plus `third-party/ripgrep/{LICENSE-MIT,UNLICENSE,SOURCE.json}`.

## Child DOX Index

No child AGENTS.md files yet.

## TUIKit supplemental notices

TUIKit 0.10.1 is a bundled package. Canonical evidence records its exact digest, MIT package declaration, and supplemental font notices. Notice generation includes every listed supplemental file; verification rejects missing files. SPDX preserves MIT as declared and the recorded aggregate conclusion without relabeling the embedded font inventory. Keep the existing runtime-version evidence independent of this dependency addition.
