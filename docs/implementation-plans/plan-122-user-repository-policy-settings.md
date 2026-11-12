# User-owned repository policy settings

**Status:** Planned. Implementation has not started.
**Delivery track:** Maintenance — extend the existing configuration and permission paths.
**Prerequisites:** Existing repository permission storage, configuration permission metadata, command policy, and repository binding. Preserve the current outer-shell admission behavior of `run_process`.
**Date:** 2026-11-12.
**Writing standard:** ASD-STE100 Simplified Technical English. Code identifiers, policy terms, and file names are technical names.

## 1 Objective

Let the user define command rules for one repository in the existing user-owned repository folder.
Use the same folder as the repository permission file and prompt overrides.
Keep permission settings outside the checkout.

Use the existing support for repository-specific executable lists and file access grants.
Explain which settings can be in checkout configuration and which settings require trusted storage.
Separate proposed product defaults from personal defaults and repository-specific permissions.

## 2 Architectural Context

Read these contracts before implementation:

- Root `AGENTS.md` and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).
- [Planning governance](planning-governance.md) and [shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).
- [ADR-11](../architecture/adr-11-central-tool-policy-pipeline.md) and [ADR-63](../architecture/adr-63-shared-command-policy.md).
- [Repository permissions](../operations/repository-permissions.md), [configuration authority](../operations/security-policy.md), and [command policy](../operations/command-policy.md).
- [Prompt override storage](../operations/prompts.md#repository-and-model-overrides).
- Acceptance scenarios SC106-01 through SC106-07 and manual procedure MTP-SC106.

Use the existing policy loader, compiler, settings writer, and repository binding.
Do not add a second permission store or execution path.
Keep filesystem access in the host. Keep rule compilation in `Threadsmith.Policies`.
Do not change completed milestone contracts.

## 3 Scope

- Accept ordinary command rules in user-owned repository permissions.
- Apply that rule layer after global user rules.
- Preserve machine-enforced restrictions.
- Verify existing per-repository executable lists and read/write folder grants through their actual tools.
- Document permission source metadata and configuration precedence.
- Include the new source in policy inspection, revision checks, reload, and managed updates.
- Define a review procedure for product defaults and personal configuration changes.
- Keep the existing repository identity, file format, directory protection, and prompt override locations.

## 4 Non-Scope

- Permission grants from checkout configuration, repository session files, or model-written files.
- A new shell parser, matcher language, operating-system sandbox, or tool execution path.
- A new slash command, modal, settings framework, or configuration watcher.
- A change to the current list replacement rules.
- Automatic edits to personal configuration during startup or upgrade.
- Automatic promotion of personal Allow rules to product defaults.
- Relocation of existing exact persistent approvals or secrets.
- Changes to the accepted sources for `tools.runProcess.shellExecutable`.

## 5 Current State

Check these facts against the active checkout before implementation.

| Setting or function | Current behavior | Required work |
|---|---|---|
| `tools.runProcess.commandPolicy` | Accepts machine and global user documents only. | Add the user-owned repository source. |
| `tools.allowedExecutables` | Already accepts user-owned repository values. | Verify its separate use by native tools and the outer shell. |
| `tools.readFile.allowedFolders` | Already accepts user-owned repository values. | Verify repository isolation and documented path resolution. |
| `tools.writeFile.allowedFolders` | Already accepts user-owned repository values. | Verify direct-write grants without granting reads or source edits. |
| `prohibitedPaths` | Already accepts user-owned repository values. | Preserve its existing precedence and enforcement. |
| `editableRoots` | Retained as a legacy setting. It does not confine ordinary source mutations. | Do not present it as the file access control for this feature. |
| `RepositoryPermissionStore` | Owns repository identity, `permissions.json`, and coordinated updates. | Reuse it for the new subtree. |
| `ConfigurationPermissionAttribute` | Declares permission sources and value shapes in code. | Extend the command-policy declaration only. |
| `PolicyCompiler` | Compiles defaults, machine rules, and user rules. | Add one explicit source layer with its own provenance. |
| `PolicyConfigurationService` | Validates typed rule and grant changes. | Include repository rules when validating every update. |

For `run_process`, `allowedExecutables` admits the selected outer shell.
The command policy controls contained commands, interpreters, and scripts.
Native Git tools still require `git` in `allowedExecutables`.
Script filenames need no list entry solely for `run_process`.

## 6 Proposed Design

### 6.1 Storage and identity

Use the existing structure:

```text
~/.threadsmith/repositories/<repository-key>/
    permissions.json
    prompts/
```

Use `RepositoryPermissionStore.GetPath` to select the permission file.
Do not accept a destination path from the model or a configuration document.
The repository key uses the canonical checkout path and the existing hash rules.
Keep the stored repository identity check.
Two checkout paths must not share permissions, even if their folder names are equal.

Keep the existing envelope: `schemaVersion`, `repositoryIdentity`, `revision`, and `settings`.
Place the command policy at `settings.tools.runProcess.commandPolicy`.
Keep prompt files in `prompts`; do not combine prompt content with permission JSON.
Do not add a second discovery location under the application directory.

### 6.2 Source classification

Keep `ConfigurationPermissionAttribute` as the source declaration.
Keep `ConfigurationPermissionCatalog` as the common reader of that declaration.
Do not add separate setting-name lists to loaders or tools.

Add `UserRepository` to the accepted sources for command policy.
Keep machine and global user support.
Do not add launch, checkout, or repository-session support for that setting.
Retain the existing accepted sources for executable and folder settings.

Ordinary preferences keep their current configuration paths.
Permission properties use trusted sources permitted by their declarations.
The user cannot change a setting's source classification through JSON.

Document three distinct cases:

1. Ordinary preferences can use checkout configuration when their owning contract permits it.
2. Permission settings can use secure repository storage only when metadata permits `UserRepository`.
3. Some permission settings remain machine-only or machine/user-only.

Do not describe every permission setting as available at repository scope.
Preserve current rejection or filtering behavior for unsupported sources.
In particular, checkout command-policy documents remain invalid.

### 6.3 Command rule precedence

Compile ordinary command rules in this order:

1. Shipped defaults.
2. Machine configuration.
3. Global user configuration.
4. User-owned repository permissions.

Within each document, keep common, platform, and shell ordering.
A later rule with the same ID replaces the complete ordinary rule.
Different IDs remain separate rules. Normal conflict and override checks still apply.
`disabledRuleIds` can remove inherited ordinary rules.
Deleting a local replacement exposes the inherited rule again.

Machine-enforced rules remain independent constraints.
Repository rules cannot replace, disable, or grant exemption from those constraints.
Reject `enforced` and `requireEnforcedMatch` in the repository policy document.

Keep exact persistent approvals in their current global user store for this change.
Do not accept `alwaysAllow` records in the new repository layer.
Repository rules can still use all five policy outcomes.
Validate saved approvals against the complete effective rule set, including repository rules.
Keep the existing exact command, repository context, and rule binding.

### 6.4 Executable and folder lists

Use the existing list resolution code.
The effective ordinary order remains defaults, machine, user, user-owned repository, then supported launch settings.
Explicit machine enforcement still restricts the result.

A supplied array replaces the inherited array. It does not append.
An absent property inherits its value.
An empty array grants no entries for that list.
Do not add implicit union, append, or remove syntax.

Resolve relative folder grants against the active checkout, as the current loader does.
Do not resolve them against the home-folder permission file.
Keep read and write grants separate.
Preserve protected paths, prohibited paths, link checks, and child restrictions.

Use this illustrative `settings` fragment in the documentation:

```json
{
  "tools": {
    "allowedExecutables": ["pwsh", "dotnet", "git", "gh"],
    "readFile": { "allowedFolders": ["C:/reference/Example"] },
    "writeFile": { "allowedFolders": [".inbox"] },
    "runProcess": {
      "commandPolicy": {
        "schemaVersion": 1,
        "platforms": {
          "windows": {
            "shells": {
              "pwsh": {
                "rules": [
                  {
                    "id": "repository-selection",
                    "policy": "Allow",
                    "matchers": [
                      { "type": "glob", "fact": "ExecutableName", "value": "Select-*", "ignoreCase": true },
                      { "type": "exact", "fact": "CommandKind", "value": "shell-command" }
                    ]
                  }
                ]
              }
            }
          }
        }
      }
    }
  }
}
```

This fragment is not a complete permission file.
The host owns the identity envelope.
The executable and folder arrays are complete replacement examples.
Use platform-appropriate paths in Unix examples.
A rule applies to the selected repository binding; a working-directory matcher can narrow it further.

### 6.5 Compilation, updates, and activation

Add the repository document to the compiler inputs and configuration revision.
Validate every profile, including inactive platform and shell profiles.
Use the existing policy byte, depth, rule, and matcher limits.
Also enforce the permission envelope's existing byte limit.
Check the final serialized representation before publication.

Add a distinct repository source label to effective rule provenance.
Include source changes in update conflict checks, even when effective outcomes are equal.
Do not discard repository rules when a global user grant or machine rule is written.

Extend the existing typed update service with a repository destination for ordinary rule changes.
Resolve its destination from the current host repository binding.
Use the existing permission store and settings coordinator to preserve identity, revision, and unrelated settings.
If a coordinated transform needs more access, extend that existing boundary.
Do not copy its envelope writer into command-policy code.
Avoid nested acquisition of the same publication gate.

Read all rule layers for candidate validation.
Reject stale revisions and invalid candidates before writing.
After publication, rebuild authority from all sources through the existing loader.
Keep disk publication and live activation as separate results.
Do not claim activation if a full repository rebind is still required.

Reuse `/command-policy <shell> reload` and the repository reopen path.
Use the existing restart rules for executable and startup-bound settings.
Do not change an operation's captured authority while it runs.
Repository switches and policy refreshes must invalidate stale approvals through the existing lifecycle.

### 6.6 Product defaults and personal settings

Create a review table before moving personal rules.
For each entry, record its purpose, consumers, current scope, and proposed destination.
Use four destinations: shipped default candidate, machine setting, global user setting, or secure repository setting.

Common filtering or inspection operations can be default candidates.
Do not promote the current broad `gh` Allow merely because `gh run view` is common.
Do not add a command parser to distinguish those operations.
Record the exact scope possible with the existing matchers.
Select and test any default change separately before including it.

Keep checkout-specific script paths in secure repository rules.
Keep general personal executable and folder permissions in global user configuration.
Move cross-repository read grants only after the user identifies which repository needs them.
Do not infer that every current global grant belongs to the active checkout.

## 7 Public Contracts

- Retain permission-envelope version 1 and command-policy version 1 unless implementation proves a version change is necessary.
- Add the declared `UserRepository` command-policy source and compiler input.
- Add the repository destination to typed ordinary rule updates.
- Preserve existing callers that supply no repository policy document.
- Preserve list replacement, all five command outcomes, and machine-only enforcement.
- Preserve current exact-grant storage and revocation behavior.
- Expose the effective source through existing user-only inspection results.

The new source is a host-owned distinction, not a JSON trust flag.
Do not expose filesystem paths through the policy library's destination enum.

## 8 Project/File Changes

| Owner | Planned change |
|---|---|
| `src/Threadsmith.Core/PermissionConfiguration.cs` | Permit the new source on the command-policy property. |
| `ConfigurationPermissionAttribute.cs` | Reuse the current source enum and catalog. Change only if required by a demonstrated gap. |
| `RepositoryPermissionStore.cs` and `RepositorySettingsCoordinator` | Reuse identity checks and coordinated publication. Extend the existing transform boundary if necessary. |
| `src/Threadsmith.Policies/Evaluation/PolicyCompiler.cs` | Compile and identify the repository layer after user rules. |
| `src/Threadsmith.Policies/Configuration/PolicyConfigurationService.cs` | Validate all sources and route typed ordinary rule changes to the selected destination. |
| `src/Threadsmith.App/SecurityPolicyConfigurationLoader.cs` | Read the new source and retain existing list resolution. |
| `RepositoryScopedBindingCoordinator.CommandPolicy.cs` | Preserve all layers during writes, reload, and activation. |
| Existing command-policy inspection and authoring owners | Show source information and the secure destination without a separate UI path. |
| Policy, architecture, and tool test projects | Verify source isolation, precedence, publication, and actual tool admission. |

Inspect actual callers before changing signatures.
Do not create duplicate configuration readers or service registrations.

## 9 Ordered Tasks

1. Trace metadata, storage, compiler, writer, reload, and tool consumers in the active checkout.
2. Record the settings and default-candidate review table. Do not change personal files yet.
3. Add focused compiler and loader tests for the new source and existing list settings.
4. Extend source admission and the compiler. Preserve machine enforcement and invalid-source checks.
5. Extend typed publication and reload. Check repository identity and complete source revisions.
6. Verify native Git tools, `run_process`, file reads, and direct writes through their existing pipelines.
7. Update user-only inspection and the planned authoring destination contract.
8. Update documentation, policy examples, SC106 acceptance behavior, and MTP-SC106 procedures.
9. Validate the final source and documentation on the required platforms.
10. If personal migration is requested, prepare the exact edits, back up both files, and verify the resulting effective permissions.

Do not start personal migration merely because the application now accepts the new source.

## 10 Testing

Use deterministic fixtures and the test runner's cancellation token.
Do not use sleeps, arbitrary test deadlines, or repeated runs to conceal failures.
Do not add assertions for specific explanatory text.

Test these behaviors:

- Repository A has a local Allow, Deny, or approval rule. Repository B does not inherit it.
- Same-named checkouts use different permission files. A copied envelope fails its identity check.
- Local rule replacement, disable, removal, and explicit overrides produce the expected decisions.
- Machine-enforced denial and closed membership remain effective against local Allow rules.
- Invalid local JSON, unsupported grant records, null policy, invalid patterns, and inactive-profile errors cannot grant authority.
- Checkout, repository-session, environment, and CLI command-policy sources remain rejected.
- Missing local arrays inherit. Present arrays replace. Empty arrays grant no list entries.
- Native Git tools require `git` in the effective executable list.
- `run_process` requires its outer shell in that list, but scripts use command rules without duplicate list entries.
- Read grants do not grant writes. Write grants do not grant reads or source edits.
- Relative folder grants use the checkout root. Link and protected-file restrictions remain effective.
- Local rule changes preserve trust settings, other permission fields, and prompt overrides.
- Global grant publication includes local rules in validation and preserves them.
- Revision conflicts, cancellation, write failures, and activation failures retain existing guarantees.
- Reload, repository switch, child operations, and headless execution use the correct complete policy snapshot.
- User-only inspection identifies the new source without leaking policy details to model context.

Use disposable configuration and inert commands. Do not use personal files or live provider calls in automated tests.
Validate JSON examples through the production parser and compiler.
Check documented accepted sources against the metadata contract, not prose snapshots.

Run the build, tests, analyzer checks, and documentation checks required by `CONTRIBUTING.md`.
Run `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`.
Record Windows, macOS, and Linux evidence separately. State unavailable checks explicitly.

## 11 Security/Permissions

Keep the entire user-owned repository directory protected from generic model mutations.
Retain path identity, link rejection, ancestor protection, and bounded file reads.
Do not let a checkout select its own trusted permission file.
No rule can grant an exception to machine enforcement or mandatory tool review.

This storage provides application permission control, not operating-system isolation.
An admitted process retains the user's account permissions.
Do not promise protection against every write made by an allowed shell or external program.

## 12 Observability

Use existing source provenance, policy inspection, and activation receipts.
Identify whether a rule comes from defaults, machine, user, or secure repository storage.
Show whether a write was saved and whether it became active.
Keep detailed rules and local paths on the existing user-only surface.
Do not add model-visible rule catalogs, workaround advice, or unbounded configuration dumps.

## 13 Migration/Compatibility

Existing installations without local command rules retain their behavior.
Do not create or modify permission files during a read.
Do not move global settings automatically.

For an explicitly requested personal migration:

1. Identify rules and list entries that belong to one repository.
2. Retain entries required by other tools, including native Git tools.
3. Prepare complete replacement arrays, including general entries that must remain available.
4. Back up global configuration and the existing permission file.
5. Write and validate the destination through the shared store before removing global entries.
6. Verify allowed and denied operations in the target repository and a separate repository.
7. Reload or restart through the supported path. Report save and activation separately.

The two files do not form one atomic transaction.
If migration stops, report which file changed and what remains to do.
Never remove a source grant before the destination is valid.

Older builds reject local command-policy settings.
Document the required application version and backup recovery procedure.
Do not describe a new permission file as compatible with an older loader.

## 14 Acceptance Criteria

- Command rules can be defined in the existing user-owned repository permission file.
- The four rule layers have documented and tested precedence.
- Local rules cannot remove machine enforcement or affect another repository binding.
- Executable and folder settings retain their current per-repository behavior and separate consumers.
- Native Git access and shell command access remain distinct.
- Managed updates, reload, and exact approvals use the complete effective rule set.
- Configuration source eligibility remains code-owned through the shared metadata.
- Documentation contains complete storage instructions and tested examples for inheritance, replacement, denial, and approval.
- No automatic personal migration or unreviewed product-default expansion occurs.
- Existing protected storage and prompt override behavior remain intact.
- Required checks pass, with platform and live-test limits reported accurately.

## 15 Risks

| Risk | Required control |
|---|---|
| A local list removes a required global entry. | Explain replacement and test complete example lists. |
| A source is omitted from grant or update validation. | Trace every compiler caller and test writes with local rules present. |
| A global rule is removed before migration is complete. | Validate the destination first and report each file change. |
| A personal Allow becomes an unnecessarily broad default. | Review the proposed default scope separately. |
| A secure file is confused with checkout configuration. | Use distinct source labels and exact storage examples. |
| An update changes trust or prompt data. | Preserve unrelated fields and use the existing store. |
| A stale policy remains active after a failed reload. | Preserve the existing binding invalidation and activation contract. |

## 16 Documentation

Update these documents during implementation:

- `docs/user-guide.md`: secure repository location, source matrix, complete list examples, and reload/restart instructions.
- `docs/operations/repository-permissions.md`: command-policy subtree and metadata classification.
- `docs/operations/command-policy.md`: source order, local rule examples, enforcement, and saved-grant limits.
- `docs/operations/security-policy.md`: accepted sources and separate executable consumers.
- `docs/architecture/adr-63-shared-command-policy.md`: the additional trusted rule source.
- SC106 acceptance scenarios and MTP-SC106: repository isolation, list behavior, and source-aware publication.
- `plan-121-command-policy-authoring.md`: the secure repository destination and its envelope instructions when that authoring work proceeds.

Link to the existing prompt storage instructions. Do not redefine prompt lookup order.
Keep source-of-truth facts in their owning documents.
Use source-repository URLs when packaged help refers to implementation plans.

## 17 Open Decisions

- Which common operations should become shipped defaults? Record exact rules and their scope before implementation of those defaults.
- Which personal folder grants belong to each repository? The current global list does not answer that question.

These decisions do not block support for the new trusted rule source.
They do block promotion or migration of the affected personal entries.
The storage location, list replacement behavior, metadata ownership, and existing exact-grant destination are fixed by this plan.
