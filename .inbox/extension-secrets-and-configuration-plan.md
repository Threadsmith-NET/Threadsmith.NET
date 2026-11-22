# Extension secret access and configuration plan

**Status:** Implemented in the host. See the [active maintenance plan](../docs/implementation-plans/maintenance-extension-secrets-and-configuration.md#17-open-decisions-and-validation) for the delivered contract, test results, and remaining release checks. Sections below preserve the original proposal.
**Delivery track:** Maintenance.
**Prerequisites:** Review the proposed contracts in this plan. Read `AGENTS.md` and the C# guardrails before implementation.
**Next plan:** [Neo4j extension plan](neo4j-extension-plan.md).
**Language:** ASD-STE100 Simplified Technical English (STE) inspired. Use short sentences and direct instructions. This document does not claim formal STE compliance. API names are technical names.

## 1. Objective

Complete two host features before work starts on the Neo4j extension:

1. Give an approved extension tool access to its approved secrets during one invocation.
2. Supply extension settings through the normal load and replacement paths.

Use the existing secret resolver, tool pipeline, and extension lifecycle. Do not create a second secret store or a second execution path.

## 2. Architectural context

The host owns permissions, approvals, secret sources, and invocation limits. The extension receives only the access needed for its current operation.

Keep the extension contract package small. Do not add a reference from that package to `Threadsmith.Tools` or the extension runtime. Keep extension implementation types out of persistent state.

Use the planning rules in `docs/implementation-plans/planning-governance.md`. This `.inbox` document is a proposal. It does not change milestone status. If the proposal becomes an active repository work item, add the implementation document and its navigation entry through the normal planning process. Do not reopen completed milestone documents.

## 3. Scope

- Add a narrow secret-access contract for extension tool execution.
- Connect that contract to the existing host secret resolver.
- Bind access to the admitted tool identity, arguments, and declared secret references.
- Stop access at completion, cancellation, timeout, or forced retirement.
- Connect extension-specific settings to normal manual loading and replacement.
- Preserve source trust and repository boundaries.
- Add contract, lifecycle, composition, and tool-pipeline tests.
- Document the supported extension configuration and secret-access behavior.

## 4. Non-scope

- Neo4j packages, connections, Cypher execution, and schema discovery.
- New secret storage providers.
- Secret creation, editing, or deletion by an extension.
- Secret access during discovery, construction, activation, or policy inspection.
- Automatic extension loading from repository instructions.
- New approval interfaces or new model tools.
- A general dependency injection container for extensions.
- Sandboxing in-process extension code.

## 5. Current state

These source references describe inspected code. Line numbers can change.

| Area | Evidence | Current behavior |
| --- | --- | --- |
| Secret declarations | `src/Threadsmith.Extensions.Abstractions/CapabilityContracts.cs:205-249` | A tool declares secret references before execution. |
| Invocation context | `src/Threadsmith.Extensions.Abstractions/CapabilityContracts.cs:147-170` | The context has no secret-resolution service. |
| Proxy execution | `src/Threadsmith.Extensions.Runtime/CapabilityProxy.cs:63-153` | The proxy forwards declarations and acquires an invocation lease. It does not supply secret access. It logs exception messages. |
| Host secret resolver | `src/Threadsmith.Tools/SecretResolution.cs:134-208,337-343` | The host already has a privileged request, source-trust controls, and an asynchronous resolver. |
| Tool admission | `src/Threadsmith.Tools/ToolInvocationPipeline.cs:521-752` | Policy, hooks, approval, budget, and scheduling checks precede execution. |
| Configuration contract | `src/Threadsmith.Extensions.Abstractions/ExtensionContracts.cs:182-195` | An extension can read its own settings through a narrow interface. |
| Load request | `src/Threadsmith.Extensions.Runtime/ExtensionRuntimeContracts.cs:8-33` | A load request can carry settings and the repository path. |
| Manual load | `src/Threadsmith.Extensions.Runtime/ExtensionHost.cs:177-213` | The normal manager path does not populate those settings or the repository path. |
| Activation | `src/Threadsmith.Extensions.Runtime/ExtensionHost.cs:718-750` | Activation already receives the settings supplied in the load request. |
| Settings view | `src/Threadsmith.Extensions.Runtime/ExtensionHostFacades.cs:37-61` | The view keeps the supplied dictionary. It does not make an independent immutable copy. |
| Selection | `src/Threadsmith.Extensions.Runtime/ExtensionSelectionConfig.cs:5-46` | Repository-owned `extensions.json` selects discovery and requested automatic loads. It has no per-extension settings map. |
| Startup | `src/Threadsmith.App/IntegrationComposition.cs:23-56` | Startup uses the selection file. It refuses repository-requested automatic code execution. |
| Replacement | `src/Threadsmith.Extensions.Runtime/HotReplacementCoordinator.cs:36-74` | Replacement loads and publishes a successor before draining the old generation. |

This inspection establishes the extension points. It is not a complete security audit. Implementation must trace the exact policy accessors, resolver composition, and retirement signals before changing them.

## 6. Proposed design

### 6.1 Secret access during execution

1. Capture and validate the declared secret references during policy admission.
2. Keep an immutable set for the exact admitted invocation. Do not call the extension again to reconstruct that set after approval.
3. After all normal admission checks pass, create an execution-only secret-access service.
4. Give the service only the admitted references, host-assigned identity, effective source restrictions, and invocation lifetime.
5. Resolve a value only when execution requests it. Use the existing `ISecretResolver`.
6. Reject a request for an undeclared reference before a provider is called.
7. Reject a request when effective extension permissions do not include secret access.
8. Reject a request after the invocation lifetime ends.
9. Discard provider results that arrive after cancellation or revocation.

Preserve the pipeline's captured security policy and registration identity checks. A copied context, a stale proxy, or a direct proxy call must not create authority. Use a host-created admission object if the current execution context cannot prove admission. Do not add a model-supplied approval flag.

The host selects component identity, purpose, repository root, minimum source trust, provider restrictions, and provider timeout. An extension must not weaken them. Preserve the resolver's existing name parsing and comparison rules.

Use `UserOwned` as the initial minimum source trust unless the owning security contract requires a stronger source. Do not silently admit a repository-owned password because its reference came from repository configuration.

Keep the service unavailable in contexts passed to resource and policy accessors. Do not place it in activation state. Link cancellation to the caller, invocation deadline, and applicable generation-retirement signals. Normal replacement must allow already admitted old-generation calls to finish under their captured state. Forced retirement must revoke access.

### 6.2 Secret values and output safety

Use an explicit value wrapper in the contract package. Its normal string representation must not reveal the value. Reveal the value only for immediate use by the extension's transport adapter.

Do not serialize the service or resolved value into context snapshots, events, results, or diagnostics. Connect resolution to the existing secret-output protection at the owning host boundary. Check extension logs and proxy exception handling as well as tool results. Replace unsafe raw exception text with stable safe diagnostics where needed for this feature.

Revocation stops new access. It cannot erase a string that extension code already copied. Document this limit. In-process extensions remain trusted executable code.

### 6.3 Extension settings

Keep extension selection separate from execution authority. Preserve the current refusal of repository-driven automatic loading.

Recommended first version: add an extension-ID-to-settings map to the existing repository-owned `.threadsmith/extensions.json` data contract. Keep values compatible with `IReadOnlyDictionary<string, string?>`. Treat this as a proposed format, not current syntax.

Do not introduce user-level extension selection. Before implementation, confirm whether connection settings also need an existing trusted user-configuration source. If they do, define source ownership and precedence before adding that source. Do not pass the whole application configuration to extensions.

Use one settings-resolution phase for manual load, reload after unload, and hot replacement. Resolve only the requested extension's section. Attach the correct repository identity. Copy and bound the settings before activation. Do not expose another extension's settings.

A generation keeps one immutable settings snapshot. Changes take effect only through an explicit load or replacement operation. Existing calls retain their old snapshot. A failed or cancelled replacement must not publish a partial settings change.

Configuration stores logical secret references, not passwords. Do not expand references when settings are loaded. Extensions must validate their own key names, types, and required values. The host must bound file size, section count, key count, and key/value lengths. Choose and document those bounds during implementation.

Missing configuration can keep the existing empty-settings behavior. Malformed new settings must not turn a safety setting off or become an unreported successful load. Define a safe diagnostic and failure policy. Preserve the current no-autoload fallback for invalid selection data.

## 7. Public contracts

The names below are proposals. Confirm them before changing the public contract.

| Contract | Required behavior |
| --- | --- |
| `IExtensionSecretAccess` | Resolve one admitted logical reference asynchronously. Accept and forward cancellation. Expose no provider selection controls. |
| Extension secret value wrapper | Require explicit value access. Use a non-secret string representation. Prevent normal JSON disclosure. |
| Extension secret failure | Return a stable safe classification or a documented safe exception. Do not include provider exception text. |
| Execution context member | Optional for compatibility, absent or denied outside execution. Do not grant authority when constructed by an extension. |
| Per-extension settings map | Use the stable descriptor ID as its identity. Preserve string/null values in the first version. |

Do not expose `ISecretResolver`, `SecretResolutionRequest`, host service providers, or implementation assemblies through the extension API. Keep DTOs in the contract assembly. Keep behavior in a small service interface and host adapter.

## 8. Project and file changes

Extend these existing owners where practical:

- `Threadsmith.Extensions.Abstractions`: invocation contract, secret-access interface, safe value/failure contract, and version policy.
- `Threadsmith.Extensions.Runtime`: `CapabilityProxy`, `CapabilityRegistry`, settings resolution, load requests, facades, and lifecycle integration.
- `Threadsmith.Tools`: admitted-reference handoff and output protection, only where the current contracts require an extension.
- `Threadsmith.App`: existing integration composition and secret-resolver wiring.
- `tests/Threadsmith.Extensions.Tests`: contract, manager, invocation, replacement, and unload tests.
- Relevant tool and application test projects: admission and composition tests.
- `tests/Threadsmith.Architecture.Tests`: dependency and public-boundary tests.

A small host adapter is justified because the contract assembly cannot reference the host resolver. Do not create another resolver implementation or another tool pipeline.

## 9. Ordered tasks

1. Trace secret declarations through the policy engine and all tool wrappers. Locate the exact admitted-reference and identity boundary.
2. Trace resolver construction, secret-output protection, and extension retirement signals.
3. Confirm the API compatibility policy and configuration-source decision.
4. Add failing tests for denied, undeclared, stale, and cancelled secret access.
5. Add the narrow contract and host adapter. Wire them through the existing admission and proxy paths.
6. Close raw-value leaks in the affected log, exception, and output paths. Do not expand into unrelated logging changes.
7. Add the bounded per-extension settings format and immutable settings resolution.
8. Wire the resolver into normal manual load and replacement. Preserve selection and activation authority.
9. Test both features together with a real loaded fixture extension and a stub secret provider.
10. Run the final validation. Update the durable documentation and acceptance coverage for changed behavior.
11. Review the result against the exit criteria. Only then start the Neo4j plan.

## 10. Testing

### Secret-access tests

- An admitted declared reference resolves through the production registration path.
- Policy denial, approval denial, and hook denial call no secret provider.
- Missing effective secret permission calls no provider.
- Undeclared, malformed, or changed declarations cannot broaden access.
- Direct proxy execution without admission cannot resolve a secret.
- The host supplies source trust, provider restrictions, purpose, and identity.
- Missing and unavailable providers produce safe failures.
- Completion, cancellation, timeout, and forced retirement reject later access.
- A late provider result is discarded after revocation.
- Concurrent invocations cannot use each other's admitted references.
- A distinctive test credential does not appear in model results, events, logs, errors, or serialized contexts.
- No retained facade prevents collectible load-context release.

### Configuration tests

- Normal manager load gives the fixture its own settings and repository identity.
- Missing settings produce the documented empty view.
- Invalid or oversized settings fail safely with no configuration dump.
- Changes to the source dictionary do not change an active generation.
- Replacement publishes one new snapshot. Old calls keep the old snapshot.
- Failed and cancelled replacement leave the active generation unchanged.
- Repository switching cannot reuse another repository's settings.
- One extension cannot enumerate another extension's settings.
- Repository selection does not authorize automatic loading.
- A secret reference stays a reference until approved execution requests it.
- Existing extensions without secret access continue to load and execute.

Use explicit barriers and test-runner cancellation for race tests. Do not use arbitrary completion deadlines as functional assertions.

### Final validation

Read current CI workflows before submission. Follow `CONTRIBUTING.md` for restore, the pinned ripgrep fixture, build, and tests. Planned product commands include:

```text
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
dotnet format src/Threadsmith.sln --verify-no-changes --no-restore
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
```

Run focused extension, tool, application, and architecture tests as well. Record the exact commands and final revision. Report unavailable platforms and pending checks. This plan does not authorize restore, network access, builds, or tests now.

## 11. Security and permissions

A declared reference is a request, not permission. A configuration value is data, not permission. Extension loading does not grant unrestricted secret access.

Do not let repository settings lower source trust or change provider restrictions. Do not read environment variables or secret files directly inside the extension as a substitute for host resolution.

## 12. Observability

Keep the existing tool start, approval, completion, and cancellation events. Do not add a second activity stream.

Use stable diagnostic codes for denied reference, missing provider, expired access, and invalid settings. Do not record secret values or full settings maps. Reuse the resolver's safe diagnostics. Add new events only if an existing owner cannot describe a required observable state.

## 13. Migration and compatibility

Preserve current settings behavior for existing callers of `ExtensionLoadRequest`. Copy their supplied dictionary at the host boundary.

Follow the contract-version rules for new members. Test an existing extension assembly against the changed host. Do not claim binary compatibility from source compilation alone.

Do not change user configuration files automatically. Do not move secrets. Document the new settings format and explicit reload behavior.

## 14. Acceptance criteria

- [ ] A normally loaded extension receives its own bounded immutable settings.
- [ ] A normally invoked extension can resolve an admitted secret through the existing resolver.
- [ ] All denial paths prevent secret resolution.
- [ ] Undeclared and expired access fails closed.
- [ ] No secret value enters host-owned durable state or normal diagnostic output in the tested paths.
- [ ] Replacement preserves settings and invocation ownership.
- [ ] Repository selection still cannot authorize automatic code execution.
- [ ] Existing extension behavior and dependency direction remain valid.
- [ ] Relevant compiler and analyzer feedback is reviewed for the final source state.
- [ ] Required tests and documentation checks pass, or remaining limits are explicit.

## 15. Risks

- A mutable declaration can differ between approval and execution. Capture it once.
- A retained secret service can outlive an invocation. Revoke it independently of garbage collection.
- A copied credential can outlive revocation. Do not claim memory erasure or sandboxing.
- A raw exception can disclose a credential before output sanitization. Check the earlier log boundary.
- A partial reload can mix settings and policy. Publish a complete generation snapshot.
- Contract changes can break separately compiled extensions. Test and version them.

## 16. Documentation

Update the owning extension-authoring, configuration, and secret-management documents during implementation. Explain reference declaration, source trust, timing, failure behavior, and the trusted-code limit.

Add observable acceptance scenarios and manual checks only where behavior changes. Use the existing planning owners. Do not add completion history to frozen milestone contracts.

## 17. Open decisions

Resolve these before implementation of the affected part:

1. Confirm the contract names, failure shape, and binary compatibility strategy.
2. Confirm repository-only settings for the first version, or specify a trusted settings source and precedence.
3. Select settings size limits and malformed-settings diagnostics.
4. Confirm how the pipeline carries the admitted secret set without a second declaration read.
5. Confirm the revocation signal for forced retirement and non-cooperative provider completion.

**Plan preparation checks:** Source paths and current extension points were inspected. The checkout root was confirmed as `C:/source/repos/Threadsmith`. No product source was changed. No build, analyzer, test, or external service check was run.
