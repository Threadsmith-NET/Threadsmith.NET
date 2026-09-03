# Release packaging and publication

## Supported downloads

| Platform | Architecture | Artifact |
|---|---|---|
| Windows | x64 | `Threadsmith-<version>-win-x64-setup.exe` |
| Windows | ARM64 | `Threadsmith-<version>-win-arm64-setup.exe` |
| Linux | x64 | `Threadsmith-<version>-linux-x64.tar.gz` |
| Linux | ARM64 | `Threadsmith-<version>-linux-arm64.tar.gz` |
| macOS | Intel x64 | `Threadsmith-<version>-osx-x64.pkg` |
| macOS | Apple silicon | `Threadsmith-<version>-osx-arm64.pkg` |

All payloads are self-contained; .NET 10 does not need to be installed. Each payload also contains its matching official ripgrep executable under `tools/` for fast repository text search, plus upstream license/provenance notices under `third-party/ripgrep/`. The flat `prompts/` directory is required application content and must contain exactly the code-declared, case-insensitively collision-free [deployed prompt catalog](prompts.md); publishing, staging, archive extraction, and installer verification fail on a missing, stale, duplicate, or undeclared prompt asset. Trimming, Native AOT, and single-file publishing are intentionally disabled. Verify downloads with the release's `SHA256SUMS` before installation.

## Installation

### Windows

Run the standalone setup executable as an administrator. It installs under Program Files, adds that directory to the machine PATH, and registers an uninstaller. Open a fresh shell before running `threadsmith --version`. Upgrades retain the stable application identity. Uninstall removes installer-owned application files only.

### Linux

Extract the archive, inspect its scripts, then run:

```sh
sudo ./install.sh
threadsmith --version
```

The default application root is `/opt/threadsmith` and launcher is `/usr/local/bin/threadsmith`. Override them for an unprivileged installation with `THREADSMITH_INSTALL_PREFIX` and `THREADSMITH_BIN_DIR`. A new prefix must be absent or empty; upgrades require an exact matching `.threadsmith-install-root` marker, symbolic-link prefixes are refused, and an existing launcher must already be the matching Threadsmith symlink. Run `sudo ./uninstall.sh` from the extracted archive to remove only a recognized installation. These ownership checks prevent replacement or deletion of unrelated directories.

### macOS

Install the architecture-specific package with Finder or `sudo installer -pkg <file> -target /`. Files are placed in `/usr/local/lib/threadsmith` and `/usr/local/bin/threadsmith` is refreshed. Open a fresh shell before launch. Uninstall with `sudo /usr/local/lib/threadsmith/uninstall.sh`; it requires the stable package receipt and preserves user state. Unsigned development packages may require explicit local approval; production release notes must state actual signing/notarization status.

## User-owned state

Install, upgrade, and uninstall do not remove user configuration, provider/MCP secrets, sessions, repositories, extensions, skills, hook state, or caches. Those remain in the existing platform-specific user locations and configuration precedence is unchanged.

Deployed prompt files are installer-owned application assets, not user state. An upgrade replaces the complete shipped defaults and does not merge local prompt edits. Back up any prompt experiments outside the installation directory before upgrading, then review and reapply them against the new version's complete catalog.

## Operator runbook

1. Ensure the repository is clean, the intended commit is on `main`, and all normal CI jobs pass. Review `eng/release/ripgrep-assets.json`: it must name only official `BurntSushi/ripgrep` release assets, retain the approved `MIT OR Unlicense` contract with MIT selected for distribution, and pin an independently reviewed SHA-256 digest for every supported RID.
2. Run `eng/release/Test-ReleaseContracts.ps1` and locally build the host platform artifact with an explicit SemVer. Confirm the published and staged `prompts/` sets match the code catalog exactly.
3. Rehearse through `workflow_dispatch`; this builds all artifacts but cannot publish a release.
4. Configure signing credentials only in repository/organization secret storage. Never place certificates, passwords, Apple profiles, tokens, or values in source or command output.
5. Create and push an immutable annotated `v<SemVer>` tag for the exact reviewed commit.
6. The release workflow builds each matrix artifact independently. Cross-published architectures receive structural staged-payload validation and execute smoke checks only on architecture-matching runners. The aggregate job rejects missing, duplicate, unexpected, wrong-version, or partial artifact sets before calling GitHub Releases.
7. Download each attached artifact plus `release-manifest.json` and `SHA256SUMS`; independently verify checksums, version output, installation, fresh-shell PATH, scripting-worker smoke use, upgrade, uninstall, and user-state preservation.
8. If any matrix or aggregate job fails, do not manually publish a partial set. Delete any incomplete draft/release, correct the cause, create a new version/tag when immutability requires it, and rerun from the reviewed commit.

## Local commands

```powershell
& .\eng\release\Test-ReleaseContracts.ps1
& .\eng\release\Build-WindowsInstaller.ps1 -Version 0.1.0 -RuntimeIdentifier win-x64
```

Use `Build-LinuxArchive.ps1` only on Linux and `Build-MacPackage.ps1` only on macOS. Platform-native tools (`iscc`, `tar`, `pkgbuild`) and release-time HTTPS access to the exact pinned GitHub asset are required. Ripgrep is never downloaded at application runtime; a missing or checksum-mismatched release asset fails staging. `Sign-WindowsArtifact.ps1` and `Notarize-MacPackage.ps1` are explicit optional production hooks; successful verification is required before an artifact may be recorded as signed/notarized.

## Release-license closure and artifact gate

`eng/release/release-license-evidence.json` is the versioned, maintainer-owned approval register. `Test-ReleaseLicenseEvidence.ps1` rejects an unknown schema, duplicate/unknown/unapproved component, unapproved license expression, missing full text, missing PrettyPrompt/SQLite/ripgrep/runtime disposition, or expired/mismatched Windows self-contained decision. Repository configuration and package metadata cannot override it. Update it only with designated-release-owner review whenever an SDK, runtime, package, native tool, GitHub Action, installer, signing, or notarization input changes.

Each publish validates the exact `project.assets.json` identity/version/SHA-512 closure, then emits deterministic UTF-8 `third-party/THIRD-PARTY-NOTICES.txt` and SPDX 2.3 `third-party/sbom.spdx.json`. PrettyPrompt carries its MPL-2.0 full text and versioned source-availability URL; SQLitePCLRaw carries Apache-2.0 text and the reviewed SQLite public-domain notice. The same publish copies the SDK root's runtime `LICENSE.txt` and `ThirdPartyNotices.txt` to canonical `third-party/dotnet-runtime/LICENSE.txt` and `THIRD-PARTY-NOTICES.txt`, recording same-RID digests in `PROVENANCE.json`.

Packaging runs only after `Test-ReleaseCompliance.ps1` passes. Every archive/installer gets a `.compliance.json` sidecar bound to its SHA-256 and staged-payload digest. `New-ReleaseManifest.ps1` rejects any missing, failed, wrong-RID, stale, or digest-mismatched sidecar, so the tag-gated workflow cannot reach `gh release create` on legal uncertainty. Inspect all six artifacts and sidecars during rehearsal; never manually bypass this gate.
