# PDF reading dependency review

Status: approved by the release owner on 2026-10-18 under ADR-49. The original candidate filename is retained for provenance.

The implementation uses PDFtoImage 5.4.0 (MIT), SkiaSharp 4.150.1 (MIT wrapper with native third-party notices), and bblanchon.PDFium desktop packages 152.0.7961 (NuGet declares Apache-2.0; native PDFium includes multiple third-party terms). The complete native bundles are deliberately not relabeled with the wrapper/package license. SPDX aggregate conclusions remain NOASSERTION for native packages.

`candidate-evidence.json` records all eight NuGet restore content hashes (`packageSha512`), separately computed signed archive SHA-512 digests (`packageArchiveSha512`), and source revisions. It also records the six official upstream PDFium archive SHA-256 digests, matching native binary digests, and notice bundle digests. Native archive binaries were compared byte-for-byte by SHA-256 with the restored NuGet assets for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and osx-arm64. The six Skia native assets are recorded separately. This verifies package contents and attribution provenance, not native execution on all platforms.

Full exact-source PDFtoImage/Skia licenses and native notices are retained beside this file. PDFium notices include every LICENSE/NOTICE payload from each matching non-V8 upstream archive; identical per-architecture files are deduplicated without removing platform differences. Each section retains its original filename and content. The PDFium source archives are pinned under `https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/7961/`; package/source links and digests are in the candidate record. No upstream license is replaced by an abbreviated summary.

The explicit approval authorizes adding these exact eight dispositions to `eng/release/release-license-evidence.json` and distributing the unmodified native dependencies with the complete accompanying notices. It does not approve modified dependency binaries or change the existing Windows .NET runtime decision. The decision is recorded for all eight components in the canonical release evidence.
