## Milestone 15 — Cross-Platform Release Packaging and Installers  *(plan 45)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Produce self-contained, installable Threadsmith.NET releases for supported Windows, macOS, and Linux targets and attach immutable downloads directly to tagged repository releases without relying on package-manager or external distribution channels.

**Deliverables:**
- Declared initial OS/architecture support matrix and stable artifact naming.
- RID-aligned, self-contained publishing of `Threadsmith.App` and `Threadsmith.Scripting.Worker`.
- Canonical validated payload layout containing required runtime, configuration, product license, and notice files plus the SHA-256-pinned official RID-matched ripgrep executable and permissive-license provenance.
- Standalone Windows setup executable with PATH, upgrade, repair/reinstall, and uninstall behavior.
- Architecture-specific macOS installer packages with explicit signing/notarization verification or honestly labeled unsigned-development mode.
- Architecture-specific Linux `.tar.gz` bundles with bounded install/uninstall scripts; optional `.deb` output may supplement but not replace them.
- Clean-environment install, launch, headless, scripting-worker, upgrade, uninstall, and user-state-preservation tests.
- Version/tag/source provenance, release manifests, SHA-256 checksums, and immutable repository-release attachments.
- Least-privilege tag-gated CI, secret-safe signing hooks, release rehearsal, operations runbook, user installation documentation, and Scenario O.

**Exit criteria:**
- Every declared supported target produces a self-contained versioned download that runs without a separately installed .NET runtime.
- Windows, macOS, and Linux artifacts install or extract the same logical validated payload, including a matching functional scripting worker, app-local ripgrep executable, and all required product/third-party content.
- Clean-environment checks pass for install, launch from a fresh shell, headless use, worker invocation, compatible upgrade, uninstall, and preservation of user-owned state.
- Artifact filenames, embedded/reported version, Git tag, source commit, manifests, and SHA-256 checksums agree; incomplete, conflicting, duplicate, or mismatched output fails publication closed.
- Required signatures/notarization verify before publication, while unsigned development artifacts are never represented as signed production releases.
- Release upload authority is unavailable to untrusted pull-request jobs, credentials remain secret, and generated binaries/installers remain outside Git history.
- All final artifacts are downloadable directly from the tagged repository release/download area; no package-manager catalog or external distribution channel is required.
- Installation/release documentation, supported-platform limitations, Scenario O, maintained manual cases, and DOX are current.

**Prerequisites:** plans 01, 20, 28, and 32. Platform packages may build concurrently on matching runners only after the canonical payload and manifest contracts are stable; one aggregate verification gate owns release attachment.

**Scope decisions:**
- Self-contained directory payloads are canonical; a single-file main executable cannot replace the separate worker and supporting assets.
- Trimming and Native AOT remain disabled until a separate compatibility investigation covers reflection, extensions, Roslyn/MSBuild, serialization, MCP, and providers.
- Windows setup, macOS package, and Linux archive are installer/download formats, not package-manager channels.
- User configuration, credentials, sessions, extensions, skills, and caches remain outside installer-owned paths and survive upgrade/uninstall.
- Signing credentials and repository-upload tokens are CI secret references only and never enter source, generated manifests, or release logs.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
