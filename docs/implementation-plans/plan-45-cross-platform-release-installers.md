# Plan 45 — Cross-Platform Release Installers

**Milestone:** M15 — Cross-Platform Release Packaging and Installers

**Prerequisites:** plans 01, 20, 28, and 32

**Depends on by:** future release signing, notarization, and installation-update work

**Status:** Implementation and repository-owned automated coverage complete; release-contract verification plus Windows x64 native and Windows ARM64 cross-published staged-payload verification pass locally. Maintained Linux/macOS clean-environment installation/upgrade/uninstall, Windows installer execution, signing/notarization, and tagged-release rehearsal remain. Canonical same-RID self-contained staging, pinned official RID-matched ripgrep inclusion, Windows Inno Setup, macOS packages, Linux archives with bounded scripts, aggregate manifests/checksums, tag-gated GitHub release attachment, payload/worker/native-search smoke checks, and release operations guidance are implemented.

## 1 Objective

Produce versioned, self-contained Threadsmith.NET release bundles and standalone installers for supported Windows, macOS, and Linux targets, then attach those files directly to a tagged repository release so users can install Threadsmith without a separately installed .NET runtime or an external package-distribution channel.

## 2 Architectural Context

`Threadsmith.App` is the executable composition root and targets .NET 10. Its deployed application is not only the main launcher: the optional isolated C# scripting capability starts the colocated `Threadsmith.Scripting.Worker` apphost, fast literal repository search starts the RID-matched installer-owned ripgrep executable, and startup depends on shipped configuration examples and runtime dependencies. Release packaging must therefore validate the complete deployed directory rather than assume that publishing one single-file executable is sufficient.

The implemented release entry point restores and publishes both product projects self-contained for one declared RID, stages both apphosts plus the exact SHA-256-pinned official ripgrep asset for that RID, requires product and third-party license/provenance content, and writes the staged-layout manifest. Platform builders, smoke checks, aggregate manifests/checksums, and tag-gated release attachment consume that canonical payload. Packaging remains a delivery concern and does not alter host authority, repository trust, extension loading, secrets, or configuration precedence.

This is an explicitly approved post-strategy milestone. Where the strategy defines runtime, security, configuration, extension, or repository behavior, those existing contracts remain authoritative.

## 3 Scope

- A declared initial support matrix for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`, with unsupported or unavailable targets reported explicitly.
- RID-specific, Release-configuration, self-contained publishing of the application and scripting worker.
- Deterministic assembly of the complete runtime payload, including the repository-pinned official RID-matched ripgrep executable, required content files, notices, licenses, and executable permissions.
- A Windows standalone setup executable with install, PATH integration, upgrade, repair/reinstall, and uninstall behavior.
- A macOS installer package for Intel and Apple Silicon, with an explicit signing/notarization mode and honest unsigned-development output when credentials are unavailable.
- A Linux compressed archive containing the self-contained payload plus bounded install and uninstall scripts; optional native `.deb` packaging may be added only if it does not displace the universal archive.
- Clean-machine/container/VM smoke tests for archive extraction, install, launch, scripting-worker invocation, upgrade, uninstall, and preservation of user-owned state.
- Versioned artifact naming, SHA-256 checksums, release manifest/provenance, and attachment to the repository host's tagged release/download area.
- Documented local and CI release procedures, prerequisites, credential references, failure recovery, and verification.

## 4 Non-Scope

- WinGet, Chocolatey, Scoop, Homebrew, NuGet global-tool, Snap, Flatpak, AUR, apt/yum repositories, app stores, or any other distribution channel/catalog.
- A hosted updater, background update checks, silent auto-update, or an in-product marketplace.
- Committing generated binaries, installers, signing certificates, notarization credentials, or release secrets to Git.
- Native AOT or trimming unless a separate compatibility investigation proves the extension, reflection, serialization, Roslyn/MSBuild, MCP, and provider surfaces safe.
- Assuming `PublishSingleFile` removes the need to deploy the scripting worker and supporting files.
- Publishing mutable “latest” files without an immutable versioned release and checksums.

## 5 Current State

The implemented release entry point restores and publishes the application and scripting worker self-contained for one declared RID; stages and requires both apphosts, the checksum-pinned official RID-matched ripgrep executable, product content, and third-party notices; writes the component-aware staged-layout manifest; and runs matching-architecture smoke checks. Runtime composition prefers the colocated worker apphost and app-local `tools/rg(.exe)`, retaining framework-dependent worker and `PATH`-resolved ripgrep behavior only for source development. Cross-platform installer definitions, installed-layout coverage, aggregate release manifests/checksums, and tag-gated attachment are implemented; maintained clean-environment/signing/release rehearsal remains.

## 6 Proposed Design

Use one repository-owned release entry point that accepts an immutable semantic version and target RID. It publishes the application and worker self-contained for that RID, verifies and extracts the exact official ripgrep archive selected by the repository-owned six-RID manifest, assembles the canonical staging layout, validates expected files/licenses/provenance and architecture, smoke-tests from staging, and delegates only the final platform-native packaging step to the matching operating-system runner.

Treat the staged directory as the canonical product payload. The Windows setup executable, macOS package, and Linux archive install the same logical contents and expose a `threadsmith` launcher. Keep user configuration, credentials, session data, extensions, skills, and caches outside the installation directory so upgrade and uninstall do not silently destroy user-owned state.

A release workflow runs only for an explicitly selected/tagged version, verifies that the tag and product version agree, builds each matrix entry on a suitable runner, downloads all immutable outputs into a release-assembly job, verifies their manifests/checksums, and attaches them to that tag's repository release. Re-running a release must fail closed on conflicting assets unless an explicit documented recovery process removes an incomplete draft release; it must never silently replace a published immutable artifact.

Signing and notarization are optional capabilities controlled only by CI secret references. Signed production output must verify successfully before attachment. When credentials are absent, local/development packaging may emit clearly named unsigned artifacts, but the workflow must not claim that they are signed, notarized, or suitable for a production release.

## 7 Public Contracts

No runtime public API is required. Release-facing contracts are versioned build inputs and artifacts:

- Supported-target manifest: RID, operating system, architecture, payload kind, installer kind, and signing requirement.
- Canonical staged-layout manifest: relative path, size, SHA-256 digest, executable bit where applicable, and component identity, including `ripgrep` for the app-local native search executable and notices.
- Pinned ripgrep asset manifest: exact upstream repository/version/license expression, selected permissive license, RID/archive/root/executable mapping, and reviewed SHA-256 digest.
- Release manifest: product/version/tag, source commit, .NET SDK/runtime identity, target matrix, artifact filenames, sizes, SHA-256 digests, signature/notarization state, and build timestamp/provenance.
- Stable artifact names such as `Threadsmith-<version>-win-x64-setup.exe`, `Threadsmith-<version>-osx-arm64.pkg`, and `Threadsmith-<version>-linux-x64.tar.gz`.
- Installer exit codes and documented install/uninstall command-line options.

Release manifests contain no credentials, tokens, repository secrets, machine-user paths, or unredacted environment dumps.

## 8 Project/File Changes

- `src/Threadsmith.App` and `src/Threadsmith.Scripting.Worker` — publish metadata and RID-aligned worker payload composition; no runtime behavior change beyond what installed-layout correctness requires.
- Repository build/release scripts under a dedicated build directory — version validation, matrix publishing, canonical staging, checksums, manifests, and local orchestration.
- Platform installer definitions/scripts under dedicated Windows, macOS, and Linux packaging directories.
- `.github/workflows` — build-only pull-request validation and explicit tagged-release packaging/attachment automation.
- Dedicated packaging tests and smoke fixtures under `tests/`; project-level assets are copied to output when newer.
- README installation section, `docs/user-guide.md`, release operations documentation, manual test plan, roadmap/status, Scenario O, and DOX when implementation lands.

Do not place generated artifacts in the repository root or commit them to source control.

## 9 Ordered Tasks

1. Inventory application startup, native/runtime dependencies, worker launch resolution, content files, writable state locations, extension/skill discovery, licenses, and current cross-platform CI behavior.
2. Record an ADR for the canonical staged-layout and platform installer choices, including why trimming, Native AOT, and main-executable-only packaging are excluded initially.
3. Define the support matrix, version source, artifact names, staged-layout manifest, release manifest, and signing/notarization states.
4. Make application and worker publishing RID-aligned and self-contained; remove fragile dependencies on framework-dependent intermediate output paths.
5. Implement the repository-owned publish/stage/checksum scripts with deterministic inputs, clean-output enforcement, secret-safe logs, local dry-run support, and checksum-verified extraction of the matching official ripgrep asset.
6. Implement Windows setup packaging and test PATH, side-by-side/conflicting version handling, upgrade, repair/reinstall, uninstall, locked files, and user-state preservation.
7. Implement macOS `.pkg` packaging, launcher installation, permissions, architecture validation, signing/notarization hooks, verification, uninstall guidance, and honest unsigned behavior.
8. Implement Linux `.tar.gz` packaging with bounded install/uninstall scripts, executable permissions, prefix selection, PATH launcher management, upgrade behavior, and user-state preservation.
9. Add clean-environment installed-layout smoke tests for launch, `--help`/version output, headless startup, worker invocation, configuration examples, extension probing, upgrade, and uninstall.
10. Add tag-gated CI matrix builds, artifact retention, aggregate manifest/checksum verification, and immutable attachment to the repository release/download area.
11. Add failure-path tests for version mismatch, missing worker/content, wrong architecture, checksum mismatch, absent/invalid signing credentials, duplicate assets, partial matrix failure, cancellation, and rerun recovery.
12. Update installation/release documentation, Scenario O, maintained manual cases, status, and DOX; run a release-candidate rehearsal before declaring M15 complete.

## 10 Testing

Automate script/unit tests for version and manifest generation, target validation, staged-layout completeness, checksums, path quoting, clean-output rules, and secret redaction. On each matching OS/architecture available to CI, test the published application from a path containing spaces and non-ASCII characters.

Exercise installers in clean ephemeral environments where practical. Verify first install, launch from a fresh shell, architecture/version reporting, headless operation, scripting-worker invocation, upgrade from the previous compatible release fixture, repair/reinstall, uninstall, idempotent cleanup, and preservation of user-owned configuration/data. Verify that uninstall removes only installer-owned files and launchers.

Release-workflow tests must prove that pull requests cannot publish releases, tag/version mismatch fails, a partial matrix does not publish a complete release, checksums match downloaded assets, signatures/notarization are verified when required, duplicate immutable assets fail closed, and secrets never appear in logs or manifests. Maintain manual real-OS checks for Windows elevation/PATH refresh, macOS Gatekeeper/notarization, Linux permissions/prefixes, and repository-host downloads.

## 11 Security/Permissions

Release jobs use least-privilege repository permissions and receive upload authority only in the final tag-gated job. Signing certificates, passwords, API tokens, and notarization credentials are CI secret references and must never enter command lines when a safer file/stdin/environment mechanism exists, generated files, logs, manifests, caches, or uploaded diagnostic artifacts.

Installer scripts reject traversal, unsafe symlink/reparse-point destinations, unexpected ownership, and unbounded recursive deletion. Uninstall operates only on a recorded installer-owned manifest and never removes user configuration, credentials, sessions, repositories, extensions, skills, or unrelated PATH entries. Installation must not weaken OS security controls, silently elevate, disable Gatekeeper/antivirus, or download/execute additional code.

Third-party installer tooling is version-pinned and provenance-reviewed. Release payloads include the authoritative Apache License 2.0 `LICENSE` text and applicable third-party notices.

## 12 Observability

CI records target RID, source commit, product/tag version, SDK version, publish mode, staged-layout verification, artifact name/size/digest, signing/notarization state, smoke-test result, and attachment outcome. Logs do not include secrets, credential locations, full environment dumps, user-owned configuration, or runtime session data.

The aggregate release manifest allows a downloaded installer to be correlated with its exact source commit and checksum without contacting an external distribution service.

## 13 Migration/Compatibility

Initial installers establish stable installation locations, launcher names, user-state boundaries, artifact names, and upgrade codes/package identifiers that later releases must preserve. Changes to those identities require explicit migration and upgrade tests.

The installed application must preserve existing configuration precedence and user-owned storage. Extension, skill, MCP, hook, provider, and persistence compatibility remain governed by their existing version contracts. Unsupported operating-system/architecture combinations fail clearly rather than falling back to an incompatible payload.

## 14 Acceptance Criteria

- A tagged release produces self-contained, versioned artifacts for every declared supported target without requiring users to install .NET 10 separately.
- Windows receives a standalone setup executable, macOS receives architecture-specific installer packages, and Linux receives architecture-specific compressed archives with bounded install/uninstall scripts.
- Every installed payload includes and successfully launches the matching scripting worker and RID-matched ripgrep executable, contains all required runtime/configuration/license/provenance content, and uses the installer-owned `tools/rg(.exe)` ahead of any source-development `PATH` fallback.
- Clean-environment tests prove install, launch from a fresh shell, headless use, worker invocation, compatible upgrade, uninstall, and preservation of user-owned state.
- Artifact names, embedded/reported versions, release tag, source commit, manifests, and SHA-256 checksums agree; wrong, missing, duplicate, or partial artifacts fail release publication closed.
- Production signing/notarization, when configured as required, is verified before attachment; unsigned output is never mislabeled as signed or notarized.
- Release files are attached directly to the tagged repository release/download area and no package-manager or external distribution channel is required.
- Generated binaries/installers and all credentials remain outside Git history; release logs and manifests are secret-free.
- CI build validation remains available independently of release publication, and untrusted pull-request execution cannot obtain signing or release-upload authority.
- Installation/release documentation, Scenario O, maintained manual checks, support matrix, limitations, and DOX are current.

## 15 Risks

- Worker/runtime or native-search files are omitted or built for a different RID: publish each component explicitly for one target and validate the canonical manifest plus real worker/ripgrep invocation.
- An upstream native archive changes or is replaced: pin the official archive SHA-256 digest and license/source metadata in the repository, verify before extraction, and fail staging closed on any mismatch.
- Reflection, extensions, Roslyn, or serializers break under optimization: keep trimming and Native AOT disabled until separately proven.
- Installer identity changes break upgrades: establish stable product/package identifiers and test previous-to-current upgrades.
- macOS downloads are blocked by Gatekeeper: support signing/notarization and label unsigned development packages honestly.
- Linux has no universal installer standard: make the self-contained archive and bounded scripts the required baseline; treat native distro packages as optional later work.
- PATH changes appear only in new shells: document and test fresh-shell behavior rather than mutating the current parent shell.
- Release credentials leak through tooling: use secret references, least privilege, redacted logs, and post-build secret-canary scans.
- A matrix job publishes a partial release: separate build from one aggregate verification/attachment gate.

## 16 Documentation

Document the supported OS/architecture matrix, exact downloadable filenames, checksum verification, install/upgrade/uninstall steps, installation and user-data locations, PATH behavior, signing/notarization status, known platform warnings, offline behavior, and troubleshooting. Add an operator release runbook covering prerequisites, local dry runs, tag/version rules, credential setup by reference, release-candidate rehearsal, attachment verification, cancellation, and incomplete-release recovery.

Planned installer behavior must remain clearly marked as unavailable until M15 implementation and release rehearsal complete.

## 17 Open Decisions

- Whether the initial Windows installer is Inno Setup or WiX; prefer Inno Setup unless MSI-specific enterprise requirements are identified before implementation.
- Whether production macOS artifacts are blocked until signing/notarization credentials exist or may initially ship with explicit unsigned warnings.
- Whether Linux `.deb` artifacts join the initial matrix; `.tar.gz` remains mandatory and sufficient for M15.
- Whether ARM64 targets are mandatory on the first production release or declared preview targets based on available runner and clean-machine coverage.
- Which repository-host release attachment API is used; keep the packaging scripts host-neutral and isolate upload mechanics in CI.
