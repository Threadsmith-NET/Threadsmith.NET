# Third-Party License Inventory and Attribution Status

## Status

**Overall status: NOT READY for public binary distribution.**

The application’s resolved NuGet libraries have only permissive or weak-copyleft
open-source license expressions suitable for use by an Apache-2.0 product:
MIT, Apache-2.0, BSD-2-Clause, and MPL-2.0. No GPL, AGPL, SSPL, Commons Clause,
or proprietary NuGet package was found in the restored solution graph.

However, the current release staging contract copies comprehensive provenance and
license material only for ripgrep. It does **not** yet stage a complete third-party
notice bundle for the NuGet runtime closure or the self-contained .NET runtime.
That is a release blocker because the applicable copyright/license/NOTICE material
must accompany redistributed binaries. The Windows self-contained runtime also
requires an explicit distribution-terms review; source/package MIT status alone is
not sufficient for that platform.

This is an engineering inventory, not legal advice. A release owner or counsel
should approve the generated notice bundle and Windows runtime distribution terms
before the first public binary release.

## Evidence and scope

| Evidence | What was checked |
|---|---|
| `Directory.Packages.props` | Central Package Management and transitive pinning are enabled; declared versions are centrally pinned. |
| `dotnet list src/Threadsmith.sln package --include-transitive --format json` | Restored dependency graph for the 44-project solution. The machine-readable snapshot is retained beside this report as `dotnet-package-graph.json`. |
| `obj/project.assets.json` and cached `.nuspec` metadata | Resolved package scope and license expressions. |
| `eng/release/ripgrep-assets.json` and `Stage-Ripgrep.ps1` | Ripgrep source, hash, license selection, and staged legal material. |
| `.NET runtime` package legal files | `microsoft.netcore.app.runtime.win-x64/10.0.10/LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`. |
| Upstream SQLitePCLRaw `LICENSE.TXT` and `NOTICE.TXT` | Apache-2.0 source license and upstream notice material requiring preservation where applicable. |
| .NET license information | .NET source/packages are MIT; published Windows product/runtime distributions use the .NET Library License rather than relying solely on the source/package MIT statement. |

The scope covers third-party material restored, built, or staged by the current
solution and release scripts. Repository-controlled extensions, downloaded skills,
MCP servers, user-installed tools, and the user’s operating system are **not**
bundled Threadsmith distributions; each must remain separately inventoried by its
supplier when it is distributed with Threadsmith.

## Shipped payload inventory

### Threadsmith.NET source

| Component | Version | License | Status |
|---|---:|---|---|
| Threadsmith.NET source and documentation | repository | Apache-2.0 | Compliant: root `LICENSE` is the authoritative product license. |

### Native release dependency: ripgrep

| Component | Version | License expression / selected license | Attribution status |
|---|---:|---|---|
| ripgrep | 15.2.0 | `MIT OR Unlicense` / **MIT selected** | **Complete.** `Stage-Ripgrep.ps1` restricts source to the pinned GitHub release, verifies archive and license-file SHA-256 values, and stages `third-party/ripgrep/LICENSE-MIT`, `UNLICENSE`, and `SOURCE.json`. |

This is a strong, reproducible attribution/provenance pattern and should be the
model for the remaining release payload.

### Product NuGet runtime closure

The restored product closure resolves only the license families below. Direct and
transitive versions are centrally constrained where declared; the exact resolved
per-project closure is in the companion JSON snapshot.

| License | Resolved product components | Suitability and redistribution status |
|---|---|---|
| MIT | `Humanizer.Core`; `Spectre.Console`; `Spectre.Console.Cli`; `TextCopy`; `Microsoft.Build.*`; `Microsoft.CodeAnalysis.*`; `Microsoft.Data.Sqlite`; `Microsoft.Extensions.*`; `Microsoft.Extensions.AI.Abstractions`; `Microsoft.VisualStudio.SolutionPersistence`; `System.Composition.*`; and their Microsoft/Roslyn runtime dependencies | Suitable for Apache-2.0 use. **Attribution incomplete for a binary release:** preserve applicable copyright and MIT license text in the release notice bundle. |
| Apache-2.0 | `ModelContextProtocol.Core`; `SQLitePCLRaw.bundle_e_sqlite3`; `SQLitePCLRaw.core`; `SQLitePCLRaw.lib.e_sqlite3` | Suitable for Apache-2.0 use. **Attribution incomplete:** include Apache-2.0 text and applicable upstream NOTICE material. SQLitePCLRaw’s upstream `NOTICE.TXT` was found and must be reviewed/copied as applicable. |
| BSD-2-Clause | `Markdig` | Suitable for Apache-2.0 use. **Attribution incomplete:** preserve its copyright and BSD-2-Clause text in the release notice bundle. |
| MPL-2.0 | `PrettyPrompt` 6.0.4 | Suitable weak-copyleft use, but has additional executable-distribution obligations. **Attribution/source-availability notice incomplete:** include MPL-2.0 text, copyright notice, and a clear source-availability notice/link for the exact upstream component/version. Do not modify its covered source without separately meeting MPL source obligations. |

`Microsoft.Build.Framework` is direct with `ExcludeAssets="runtime"` and
`PrivateAssets="all"`; analysis/build-only assets described below are likewise not
part of the shipped application closure. They remain relevant to source/build
compliance but do not need to be copied into the application payload unless a
release process starts redistributing them.

### Self-contained .NET runtime

| Component | Version | Status |
|---|---:|---|
| Microsoft.NETCore.App runtime (RID-specific self-contained payload) | 10.0.10 | **Incomplete release attribution.** The Windows runtime package contains `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`; neither is currently copied by the release scripts. The current .NET licensing guidance distinguishes MIT source/packages from Windows product/runtime distribution terms. Review and stage the required Windows license and notices for every Windows RID before publication. Apply the equivalent RID-specific runtime legal material for Linux/macOS too. |

## Non-shipped dependency inventory

### Build and analysis only

| Components | License | Status |
|---|---|---|
| `StyleCop.Analyzers` 1.2.0-beta.556; `Roslynator.Analyzers` 4.15.0; `Microsoft.VisualStudio.Threading.Analyzers` 18.7.23; `Microsoft.SourceLink.GitHub` 10.0.301 | MIT (StyleCop/Roslynator/Microsoft metadata reviewed) | Suitable. These are build-time tools and not staged into the product payload. Keep versions pinned and include them in any source-distribution/SBOM record. |

### Test only

| Components | License | Status |
|---|---|---|
| `xunit.v3.*` 3.2.2 and `xunit.analyzers` 1.27.0 | Apache-2.0 | Suitable; test-only, not shipped. |
| `Microsoft.Testing.Platform*` 2.3.3, `Microsoft.Testing.Extensions.*` 2.0.2, `Microsoft.ApplicationInsights` 2.23.0, test-only `Microsoft.Extensions.*` dependencies | MIT | Suitable; test-only, not shipped. |
| `ModelContextProtocol` 2.0.0 test server fixture | Apache-2.0 | Suitable; test-only, not shipped. |

### Spikes and centrally declared but unused packages

| Component | Scope | License / status |
|---|---|---|
| `Microsoft.Extensions.AI` 10.8.3 | `spikes/Spike.OpenAiStreaming` only | MIT per restored NuGet metadata; suitable and not shipped. |
| `Microsoft.Extensions.AI.OpenAI` 10.8.3 | Centrally declared, no current `PackageReference` | Not in the current restored closure. Remove the unused central version or add an intentional consumer before release inventory generation. |
| `Terminal.Gui` | `spikes/Spike.TerminalGui` only | Not restored in this checkout because it has no centrally declared version. Upstream project license is MIT, but this repository cannot presently prove the exact package/version it would restore. Treat as **unverified** until version-pinned and restored, or remove the obsolete spike dependency. |

## Attribution requirements by license family

| Family | Required release treatment |
|---|---|
| MIT / BSD-2-Clause | Preserve the copyright and permission/disclaimer text for each redistributed component. A grouped notice file may contain the full texts. |
| Apache-2.0 | Include Apache-2.0 text and preserve applicable copyright, patent, trademark, and NOTICE-file attributions. Do not assume the root product `LICENSE` alone satisfies a dependency’s NOTICE requirement. |
| MPL-2.0 | Include MPL-2.0 text and notices; make the covered source available by a reasonable means and tell recipients where to obtain it. Track any modification to MPL-covered source separately. |
| .NET self-contained runtime | Copy the applicable RID runtime license and third-party notices, and confirm the platform-specific distribution terms. |
| ripgrep | Current process already includes both upstream license options and a source/hash manifest; retain this in every installer/archive payload. |

## Required action items

### Release blockers

- [ ] **Generate and stage a complete third-party notice bundle.** Add a deterministic release step that resolves the exact publish closure for each RID and stages a top-level `THIRD-PARTY-NOTICES.txt` (or equivalent directory) containing the required MIT, BSD-2-Clause, Apache-2.0, MPL-2.0, SQLitePCLRaw NOTICE, and component copyright/source entries. Do not use a hand-maintained partial list as the authority.
- [ ] **Stage self-contained runtime legal files per RID.** Copy the runtime `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` from the exact runtime pack used by publish, validate their presence in archive/installers, and record their versions/digests in release provenance.
- [ ] **Obtain explicit Windows runtime distribution approval.** Confirm the .NET Library License and any Windows-specific runtime conditions for the selected self-contained distribution model. Record the decision in release documentation/ADR before public Windows installers are published.
- [ ] **Meet PrettyPrompt MPL-2.0 executable distribution obligations.** Put MPL-2.0 text and exact source-availability information in the staged notices; include the resolved package version and upstream source location in provenance.
- [ ] **Review and preserve SQLitePCLRaw NOTICE material.** The upstream repository has `NOTICE.TXT`; determine which notices apply to the exact `bundle_e_sqlite3`/`lib.e_sqlite3` payload and include them in the generated notice bundle.
- [ ] **Add release tests.** Fail packaging if the notice bundle, runtime legal files, ripgrep attribution directory, and source/version manifest are absent or differ from the resolved closure. Test each of the six supported RIDs.

### Dependency-governance actions

- [ ] **Fix or remove `Spike.TerminalGui`.** Add a central pinned version, restore and capture its exact license metadata, or remove the stale spike package reference. Do not treat its current license status as verified from this repository.
- [ ] **Remove unused `Microsoft.Extensions.AI.OpenAI` central declaration** unless a project intentionally begins consuming it; regenerate the inventory afterward.
- [ ] **Inventory GitHub Actions and release-time tools separately.** `actions/*@v4`, Chocolatey-installed Inno Setup, and any signing/notarization tooling are not NuGet runtime dependencies. Record their exact versions, upstream licenses, provenance, and whether they are redistributed versus build-host-only.
- [ ] **Add SBOM generation/retention.** Produce CycloneDX or SPDX from the locked/resolved graph for every release and attach/archive it with the release provenance. It should complement, not replace, human-readable license texts and required notices.

## Completed controls

- [x] Product source is Apache-2.0 licensed at repository root.
- [x] NuGet versions are centrally managed and transitive pinning is enabled.
- [x] Resolved application NuGet package metadata shows only MIT, Apache-2.0, BSD-2-Clause, or MPL-2.0 licenses; each is compatible with the product subject to the obligations above.
- [x] No GPL/AGPL/SSPL/Commons-Clause/proprietary NuGet package was found in the current restored solution graph.
- [x] Ripgrep 15.2.0 has pinned origin, SHA-256 verification, selected-license validation, and staged MIT/Unlicense/source provenance.

## Maintenance procedure

Re-run the graph and notice validation whenever a package version, project reference,
RID, runtime SDK, native binary, installer, or release script changes. The release
artifact—not just the repository—must be the object verified for legal files.
