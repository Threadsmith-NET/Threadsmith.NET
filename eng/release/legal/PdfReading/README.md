# PDF reading dependency review

## Approved baseline (152.0.7961)

The release owner approved this exact set on 2026-10-18 under ADR-49. The original candidate filename is retained for provenance.

The approved baseline uses PDFtoImage 5.4.0 (MIT), SkiaSharp 4.150.1 (MIT wrapper with native third-party notices), and bblanchon.PDFium desktop packages 152.0.7961 (NuGet declares Apache-2.0; native PDFium includes multiple third-party terms). The complete native bundles are deliberately not relabeled with the wrapper/package license. SPDX aggregate conclusions remain NOASSERTION for native packages.

`candidate-evidence.json` records all eight NuGet restore content hashes (`packageSha512`), separately computed signed archive SHA-512 digests (`packageArchiveSha512`), and source revisions. It also records the six official upstream PDFium archive SHA-256 digests, matching native binary digests, and notice bundle digests. Native archive binaries were compared byte-for-byte by SHA-256 with the restored NuGet assets for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and osx-arm64. The six Skia native assets are recorded separately. This verifies package contents and attribution provenance, not native execution on all platforms.

Full exact-source PDFtoImage/Skia licenses and native notices are retained beside this file. PDFium notices include every LICENSE/NOTICE payload from each matching non-V8 upstream archive; identical per-architecture files are deduplicated without removing platform differences. Each section retains its original filename and content. The PDFium source archives are pinned under `https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/7961/`; package/source links and digests are in the candidate record. No upstream license is replaced by an abbreviated summary.

The explicit approval authorizes adding these exact eight dispositions to `eng/release/release-license-evidence.json` and distributing the unmodified native dependencies with the complete accompanying notices. It does not approve modified dependency binaries or change the existing Windows .NET runtime decision. The decision is recorded for all eight components in the canonical release evidence.

## Upgrade candidate (160.0.8165)

The Linux, macOS, and Win32 pins move together. PDFtoImage stays at 5.4.0 and SkiaSharp stays at 4.150.1. [Candidate evidence](160.0.8165-candidate-evidence.json) records the three verified NuGet restore hashes and six attested non-V8 upstream archive hashes. Restore resolves native assets for all six desktop RIDs; runtime validation was performed on Windows x64 only.

The release owner approved the three unmodified PDFium 160.0.8165 packages with their complete matching notices on 2026-11-18. The approval is recorded in the candidate evidence and does not cover Windows .NET runtime redistribution.

Before updating the canonical evidence, hash the signed NuGet archives, match all six NuGet binaries against the attested archives, and collect and hash the exact license and notice payloads. The candidate does not satisfy the release evidence gate until those checks are complete.

### Security verification

The Google advisories describe Chrome product versions, not standalone PDFium package ranges. The baseline upstream ref is `dd3583292697a0356cca1f64892892a92f0a2770` (`chromium/7961`); the candidate is `1f02bc7d908ab301fa2a2babd87226a19311ff38` (`chromium/8165`). The publisher's [baseline](https://github.com/bblanchon/pdfium-binaries/blob/c6529b58791d142002f819beb46e370e668797d7/steps/05-configure.sh) and [candidate](https://github.com/bblanchon/pdfium-binaries/blob/83c18af693945b735503665259b728ba7d409fd0/steps/05-configure.sh) build recipes disable both V8 and XFA for the non-V8 variants.

- [CVE-2026-121166](https://chromereleases.googleblog.com/2026/11/stable-channel-update-for-desktop.html): the [bug-linked change](https://chromium.googlesource.com/chromium/src/+/7b9c6937f908b276c6a3f8c0cfe4f59de74ddc3a) maps to PDFium commit `cce5c5ed801f54cadb5b039d98ed43804994764c`, listed in [chromium/8086](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8086). The changed file, `fxjs/cjs_global.cpp`, is gated by `pdf_enable_v8` in the baseline source. That is evidence against exposure through this code path in the non-V8 build, not a claim that the upgrade remediates this CVE.
- [CVE-2026-118345](https://chromereleases.googleblog.com/2026/10/stable-channel-update-for-desktop_0562499947.html): the [bug-linked Chromium roll](https://chromium.googlesource.com/chromium/src/+/ab8026587415208339ce18b6f1414a1579fc5563) points to an OpenJPEG change listed in [chromium/8125](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8125). The exact stable backport and affected source range were not verified against the two standalone builds. Do not claim Threadsmith exposure or CVE remediation from this lead.
