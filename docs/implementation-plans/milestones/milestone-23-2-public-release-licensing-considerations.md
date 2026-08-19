## Milestone 23.2 - Public Release Licensing Considerations  *(plans 69–72)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make the canonical self-contained release payload legally attributable, provenance-backed, and fail-closed before any public installer/archive is published.

**Deliverables:**
- A versioned host-owned release-license policy and evidence register covering the exact package/runtime/native/tool closure, including the Windows self-contained-runtime distribution decision.
- Deterministic human-readable third-party notices plus an SPDX or CycloneDX SBOM generated from the resolved release closure, not a hand-maintained partial list.
- Exact MPL-2.0 source-availability/provenance and applicable SQLitePCLRaw NOTICE treatment in the generated attribution material.
- RID-specific staging of the exact .NET runtime `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`, with version/digest provenance.
- Cross-RID archive/installer tests proving Threadsmith’s Apache license, complete notice bundle, runtime legal material, and ripgrep provenance are present and match the resolved payload.
- Dependency-governance closure for the unpinned `Spike.TerminalGui` reference, unused `Microsoft.Extensions.AI.OpenAI` central declaration, and build-host/release-tool inventory.

**Exit criteria:**
- Public packaging fails before archive/installer creation when legal evidence, approved Windows decision, notices, SBOM, runtime legal files, or required provenance is missing, stale, malformed, or mismatched.
- The release uses one canonical host-owned closure manifest; no model, repository configuration, extension, or external package metadata grants attribution or release authority.
- Every supported RID has a clean staged-payload test demonstrating exact legal-file presence and digest/version binding.
- PrettyPrompt MPL-2.0 and applicable SQLitePCLRaw notices are complete, source availability is explicit, and no component license text is silently inferred from a package name.
- The supported self-contained Windows distribution model has an explicitly recorded owner-approved decision; absence or expiry blocks Windows publication.
- Release-time GitHub Actions, installer/signing tools, and retained non-product spike dependencies have a separately scoped provenance/license disposition.
- Plans 69–72, Scenario AI, release operations/docs, maintained manual checks, DOX, and all packaging gates are complete before M23.2 closes.

**Prerequisites:** Plans 20, 45, 59, and 64–68; the existing six-RID self-contained release payload and ripgrep staging contract remain authoritative inputs.

**Scope decisions:**
- M23.2 preserves the self-contained release model. Moving to framework-dependent deployment is explicitly out of scope; it would be a separate product/install decision.
- Engineering records evidence and implements deterministic gates; only the designated release owner or counsel may approve a legal distribution conclusion.
- SBOMs complement rather than replace required license/NOTICE texts.
- Repository-controlled extensions, skills, MCP servers, and user-installed tools are excluded unless intentionally bundled into a release closure.
- No package is accepted merely because it is transitive, Microsoft-authored, or available on NuGet; it must have a resolved version, license disposition, provenance, and applicable notice treatment.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
