# Extension secrets and configuration

**Status:** Active. Configuration slice implemented; validation and review recorded below. Execution-time secret access is not implemented.
**Delivery track:** Maintenance.
**Prerequisites:** [Planning governance](planning-governance.md), [shared context](00-shared-context.md), C# guardrails, ADRs 19–24 and ADR-48. The initial proposal is `.inbox/extension-secrets-and-configuration-plan.md` in the author's checkout.

## 1. Objective

Give explicitly loaded extensions their own immutable settings. Then give admitted tool invocations narrow access to approved secrets through the existing resolver.

## 2. Architectural context

Extend the existing extension host, shared activation/replacement core, tool pipeline, and secret resolver. Settings and declarations are data, not authority. Preserve the contract package's dependency direction. In-process extensions are trusted executable code, not sandboxed code.

## 3. Scope

- Bounded repository settings, manual load, explicit reload, and replacement snapshots.
- Later: execution-only secret contract, captured admission handoff, host resolver adapter, independent revocation, and output protection.
- Contract, composition, lifecycle, pipeline, and security tests for each delivered slice.

## 4. Non-scope

Neo4j functionality, new secret stores, secret editing, secret access during activation or inspection, repository-authorized autoload, new approval surfaces, general extension DI, and sandboxing.

## 5. Current state

- `ExtensionSelectionConfig` owns bounded selection/settings parsing. Invalid startup data means no automatic loads. Invalid explicit load data fails with `ExtensionSettingsInvalid`.
- `ExtensionHost` snapshots explicit settings before its first await and resolves repository settings by the activated descriptor ID in the existing load core. App composition binds the host to the repository root.
- `ExtensionConfiguration` copies its input into an ordinal frozen dictionary.
- `HotReplacementCoordinator` already uses that same load core. Publication checks activation cancellation before switching registrations.
- Secret investigation found that `DefaultPolicyEngine` validates declarations but discards the admitted set. `ToolExecutionContext` is publicly constructible. It does not prove admission.
- Invocation leases track unload ownership but are not active timeout/revocation signals. Forced retirement needs an explicit, reliable revocation boundary.
- The existing output sanitizer is pattern-based. Arbitrary resolved credentials need exact-value protection at results, logs, and exception boundaries before exposure.

## 6. Design

### Configuration slice

Use only repository-owned `.threadsmith/extensions.json`. No trusted user settings source or precedence layer is added. The optional `settings` object maps stable descriptor IDs to string/null maps. IDs and keys use ordinal case-sensitive comparison. Top-level recognized property names remain case-insensitive.

Bounds: 256 KiB UTF-8 file including BOM; JSON depth 8; 128 sections; ID/key length 256 UTF-16 code units; 128 keys per section; value length 8,192 code units; aggregate key/value length 65,536 code units per section. Duplicate root properties (ignoring case), duplicate IDs, duplicate keys, wrong JSON types, blank names, and exceeded bounds fail. Unknown root properties remain ignored, but count toward file/depth limits.

A missing file or section gives empty settings. Other read/parse failures reject explicit loads without including paths, keys, values, or parser exceptions in the diagnostic. Startup retains its safe no-autoload fallback. A supplied `ExtensionLoadRequest.Configuration` overrides repository settings, including an explicitly empty map. Null selects repository settings. Supplied settings are copied before awaiting; each activated view is independently immutable.

The composed host shares the existing live session policy binding. Its discovery and normal manager loads fail closed with `ExtensionRepositoryStale` after a repository switch; restart in the new repository to refresh startup selection. Loads revalidate their captured policy before publication. Proxies reject calls targeting a different repository from their activation snapshot. This deliberately avoids a second repository-rebinding lifecycle.

Each load or replacement resolves fresh settings. An active generation never rereads them. Failed or cancelled activation must not publish settings or tools. Publication is the cancellation commit boundary; activation-completion notification is published without the cancelled caller token after that boundary. Normal replacement lets old calls finish on their own view.

### Secret slice — pending

Capture and validate declarations once during policy admission. Freeze the exact references, input and host-assigned identity. Carry opaque, host-created, non-serialized admission authority to the existing execution boundary only after policy, hooks, approval, budget, scheduling, deadline and registration checks. A copied context, direct proxy call, stale proxy, or changed declaration cannot mint authority. Custom policy engines which do not capture declarations must fail closed for secret access.

The execution-only adapter uses the shared `ISecretResolver`, `UserOwned` minimum source trust, host-selected identity/purpose/repository/provider restrictions and timeout. Preserve existing reference parsing and comparison semantics. Reject undeclared or unpermitted references before calling a provider. Do not expose provider controls or host implementation types through extension contracts. Normal manager loading currently grants only `TrustedRead`; do not silently raise trust to grant secret permission.

Use an optional invocation-context member, narrow `IExtensionSecretAccess`, explicit safe value wrapper and stable safe failure shape. Keep values/services out of serialization and activation/policy contexts. Revoke on completion, cancellation, absolute deadline and forced retirement. Discard late non-cooperative results. Ordinary draining must not revoke already admitted old-generation calls. A copied string cannot be erased by revocation.

Register exact resolved-value output protection before revealing a credential. Cover JSON-escaped output, provenance, retained extension loggers and raw proxy exceptions as well as final tool results. Do not add a competing resolver, pipeline or activity stream.

## 7. Public contracts

Configuration uses the existing extension configuration interface. No change to `Threadsmith.Extensions.Abstractions` in this slice. `ExtensionLoadRequest.Configuration` is nullable to distinguish repository selection from an explicit empty override. The runtime host accepts an optional repository path; it never infers settings identity from process working directory.

Pending secret names and failures remain proposals. Follow the additive-major-version policy. Test a genuinely separately compiled old extension before claiming binary compatibility; rebuilding current fixtures is not that test.

## 8. Project and file changes

Current owners: `ExtensionSelectionConfig`, `ExtensionHost`, `ExtensionHostFacades`, `ExtensionRuntimeContracts`, App `IntegrationComposition`, extension settings fixture/tests, authoring/security docs, and Scenario D.

Later owners: `CapabilityProxy`, `CapabilityRegistry`, tool admission contracts/pipeline, secret output protection and resolver composition, generation retirement, architecture and tool/application tests.

## 9. Tasks

- [x] Trace declarations, resolver/output protection, registration identity and retirement signals.
- [x] Choose repository-only settings and safe bounds/failure policy.
- [x] Extend normal manual load and shared replacement with immutable settings.
- [x] Add real loaded-fixture settings and cancellation tests.
- [x] Validate configuration source with scoped build/tests/analyzers and adversarially review it; record results and remaining release-wide checks below.
- [ ] Define opaque admitted-reference handoff and explicit retirement revocation.
- [ ] Add failing secret denial, stale/replay, cancellation and leak tests.
- [ ] Implement narrow contract and shared-resolver adapter through normal admission/proxy paths.
- [ ] Test effective permissions without silently raising normal manual-load trust.
- [ ] Close affected raw log/exception/result leaks and test exact-value protection.
- [ ] Test normal-manager secret resolution, replacement races, collectible unload and old-binary compatibility.
- [ ] Complete whole-plan validation and secret documentation before starting Neo4j implementation.

## 10. Testing

`ExtensionSettingsTests` covers manual load identity/isolation, missing sections, explicit immutable overrides, changed source settings, successful replacement snapshots, failed/cancelled replacement and cross-repository identity. Parser tests cover malformed types, duplicates, counts, lengths, aggregate size, byte limits, depth and BOM. Existing startup architecture tests must continue to prove no autoload construction. Scenario D owns observable settings acceptance. MTP-EXT-SETTINGS covers explicit operator verification and the restart-after-repository-switch limit.

Follow `CONTRIBUTING.md` and current `.github/workflows/build.yml`: restore; stage pinned ripgrep; Debug solution build; solution tests; formatting and suggestion-level analyzer checks; packaged-documentation preflight. Run extension and architecture tests explicitly. Record commands and limitations, never infer cross-platform results from Windows validation.

Pending secret tests must prove every admission-denial path calls no provider, declarations cannot broaden after approval, direct/copied/stale authority fails, caller/deadline/completion/forced-retirement access expires, late results are discarded, concurrent invocation scopes remain isolated, and distinctive credentials stay out of host results/events/logs/serialization. Use explicit barriers and runner cancellation, not functional completion deadlines.

## 11. Security and permissions

Settings must contain logical secret references, not passwords. Loading does not expand references or grant access. Extensions validate their own key meanings and required values. Repository settings cannot lower source trust, select privileged providers, or authorize code execution.

## 12. Observability

Reuse load and activation events and the existing tool activity stream. Configuration failures use the fixed `ExtensionSettingsInvalid` diagnostic without configuration dumps. Secret failures need stable classifications and safe diagnostics through existing owners.

## 13. Migration and compatibility

No files are automatically rewritten. Existing explicit configuration callers retain their values through copied snapshots. Missing configuration remains empty. Changes take effect only on explicit unload/load or replacement. No extension contract interface changed in the configuration slice; old-binary secret-contract testing remains pending.

## 14. Acceptance criteria

- [x] Configuration slice passes final solution build, affected suites/analyzers and clean-context adversarial source review.
- [ ] Complete remaining release-wide validation listed below.
- [ ] A normally invoked extension resolves only admitted secrets through the shared resolver.
- [ ] Every denial path prevents provider access; undeclared/expired access fails closed.
- [ ] No resolved secret enters host-owned durable state or normal diagnostic output in tested paths.
- [ ] Replacement preserves invocation/settings ownership; repository selection never grants autoload.
- [ ] Compatibility and dependency direction remain valid.
- [ ] Relevant compiler/analyzer, test and documentation checks pass, with platform limits explicit.

## 15. Risks

Mutable declarations, retained secret services, late provider results, raw early logs, partial replacement and separately compiled binary compatibility require explicit tests. ALC isolation is not a security boundary. Revocation cannot erase copied credentials.

## 16. Documentation

Configuration behavior is documented in `docs/extension-authoring/authoring-guide.md` and the operations security-policy catalog. Update the owning secret-management/tool-contract docs when secret access is implemented, not before. Do not add completion history to frozen milestone contracts.

## 17. Open decisions and validation

Resolved: repository-only first version; bounds/failure policy and explicit override semantics above. Pending: secret API failure shape, unforgeable admission design, effective-secret-permission normal-load path, forced-retirement revocation and exact-value output-protection lifetime.

### Configuration-slice results

Branch: `feature/extension-secrets-and-configuration`, based on `971b05226c2306330dcd9357e27af95c09d51b0e`. Work remains uncommitted; no push or PR was requested. The full proposal is not complete.

Final C# source validation on Windows:

- `dotnet restore src/Threadsmith.sln` — passed after adding the fixture project.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep` — passed; subsequent solution builds used the staged assets.
- Native `dotnet_build` for `src/Threadsmith.sln`, Debug, without restore — passed with no diagnostics after the final C# edit. Configured build analyzers ran.
- Separately invoked native `dotnet_analyzers` for `src/Threadsmith.Extensions.Runtime/Threadsmith.Extensions.Runtime.csproj` and `tests/Threadsmith.Extensions.Tests/Threadsmith.Extensions.Tests.csproj`, Debug — both passed with no reportable diagnostics.
- `dotnet test --project tests/Threadsmith.Extensions.Tests/Threadsmith.Extensions.Tests.csproj --configuration Debug --no-build --report-ctrf --report-ctrf-filename extensions-validated.ctrf --results-directory C:/source/repos/Threadsmith/.threadsmith/.scratchpad/extension-settings-validation` — 80 passed, zero failed/skipped.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --report-ctrf --report-ctrf-filename architecture-validated.ctrf --results-directory C:/source/repos/Threadsmith/.threadsmith/.scratchpad/extension-settings-validation` — 524 passed, four skipped (two macOS volume cases and two opt-in live-model cases), zero failed.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include <all 11 changed C# files>` — passed after the final C# edit.
- `git diff --check HEAD` — passed for the reviewed implementation. Git noted the existing CRLF-to-LF normalization of the manual-test document, not a whitespace error.

Clean-context security, correctness and test reviews inspected actual diffs/current source and relevant callers/base revisions. Valid findings were fixed: rollback of an activated unpublished successor; stale repository settings after `/open`; retrying manager unload after cancelled deactivation; and observable fixture cleanup. Final correctness/test reviews returned no findings; the completed security review also returned no findings. This is a clean source-review result for the configuration slice, not a full-plan or CI sign-off.

Fixture cleanup now deletes and asserts removal of test-owned configuration data. Shadow staging is separate and remains owned by the existing runtime unload lifecycle. `UnloadBlocked` staging is explicitly reported in test output; tests do not claim guaranteed deletion of JIT-retained mapped DLLs within the same process.

### Outstanding validation and limitations

- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` timed out; no full-solution test success is claimed. A later TRX-report attempt ran zero tests because the test host rejected the handshake; the supported CTRF runs above subsequently passed.
- Whole-solution native `dotnet_format_check` timed out while reporting existing formatting issues in unchanged repository-intelligence, delegation and other files. The extension-project format check also reported pre-existing missing final newlines in unchanged test files. These unrelated files were not reformatted. Final changed-file whitespace checking passed.
- Suggestion-level analyzer checking of the new parser/tests/fixture passed before the last fixture-cleanup repair. The broader information-level check reported existing suggestions in unchanged portions of touched files. Final build and separately invoked analyzer checks passed, but a final full information-level format/analyzer pass is not claimed.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` passed earlier in the session. Later documentation changed, and the host rejected the repeated identical invocation; the final documentation preflight remains outstanding. `eng/Test-PrSubmission.ps1`, manual terminal procedures, macOS/Linux checks and CI were not run. No release/PR-readiness claim is made.
- Tests drive the existing shared repository policy binding directly; review separately traced its production `/open` publication. They do not simulate the entire interactive command loop.
- Execution-time secret access, independent revocation, exact-value output protection and separately compiled old-binary compatibility remain pending. No extension contract API or secret store was added by this slice.
