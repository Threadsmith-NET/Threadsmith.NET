# TUIKit 0.10.1 candidate licensing evidence

This directory records exact TUIKit 0.10.1 licenses and attributions used by the default interactive frontend. The historical directory name is retained for evidence stability. Canonical product evidence includes the package and supplemental notices.

`candidate-evidence.json` records the NuGet archive SHA-256/SHA-512, upstream
commit, .NET 10 assembly hash, exact embedded-font resource hashes, and hashes
of the accompanying legal documents. Notice files use LF line endings. The
font headers were extracted using the FIGfont header's declared comment count
from the public manifest resources in the published assembly. No glyph data or
upstream widget implementation is vendored into the product.

## Observed terms

| Material | Evidence | Disposition |
|---|---|---|
| TUIKit code | Package declares MIT; pinned source `LICENSE.md` names Joel Christner, 2026 | Full source license preserved in `TUIKit-LICENSE.txt` |
| 3 embedded fonts | AnsiCompact, Classy, CoderMini headers say MIT | Preserve author headers and full MIT text |
| 6 embedded fonts | Future, FutureSmooth, FutureThin, Pagga, SmallBlock, SmallBraille specify WTFPL v2 | Permissive license; full text preserved in `WTFPL-2.0.txt` |
| 18 embedded fonts | Headers grant modification subject to naming the modifier | Preserve complete author headers and terms |
| 56 embedded fonts | No explicit license named in the declared comment header | Preserve the package attribution and original headers; do not infer a prohibition from an absent header license |

The assembly contains **83 font resources**. The package's
`fonts/LICENSE.figlet.txt` says each font is included under its author's terms;
it reports that a scan found no restrictive text. The preserved headers are an
inventory of observed wording, not a claim that every font has the same license.

WTFPL v2 is permissive. Its absence from the repository's existing approved
expression list is an inventory update, not a restriction imposed by that
license. No upstream clarification, issue, or discussion is required by this
implementation. The owner explicitly directed that nothing be posted externally.

`TUIKit-font-attribution.txt` and `TUIKit-font-removed.txt` preserve the package's
own statements. `TUIKit-font-headers.txt` preserves all observed author comments,
including author names and permissions. Header categories are engineering
observations. The candidate's aggregate `NOASSERTION` records that header
categorization does not assign one concluded SPDX expression to the entire
bundle; it does not indicate that redistribution is prohibited.

## Product integration and release closure

The product integration now performs the following:

1. Add exact package evidence to `../../release-license-evidence.json` through
   the existing ADR-49 release process in the same change as the production
   package reference, including supplemental notices and applicable expressions.
2. Extend deterministic notice/SPDX generation to include the MIT copyright
   text, font attributions, full applicable licenses and preserved author terms.
   An SPDX package entry or a generic MIT template alone is insufficient.
3. Regenerate the product package graph and licensing inventory; generate and
   inspect all six RID notice bundles/SBOMs and packaged payloads. Verify that
   both PrettyPrompt and TUIKit remain represented in the shipped closure.

All six isolated candidate and product self-contained publishes succeeded on 2026-09-04. Canonical product evidence, supplemental notice generation, the dependency inventory, and all six local RID notice/SPDX outputs now include TUIKit 0.10.1. No artifact was published externally.
