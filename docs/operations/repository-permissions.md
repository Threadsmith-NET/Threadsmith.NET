# Repository permissions on this computer

Project preferences belong in `<repository>/.threadsmith/config.json`. Permission choices belong outside the checkout, under your user configuration directory. A checked-in file cannot approve its own tools, processes, endpoints or automatic edits.

## Choose once

Use `/policy AlwaysTrustRepo` to remember automatic mutation approval for the current repository, or `/policy ReviewRisky` to review only changes classified as risky. The default is `ReviewAll`. `/policy ReviewAll` revokes automatic mutation approval. `/policy current` reports the requested and effective modes; a saved choice can still be restricted by enforced policy. `TrustSession` lasts only for the current session and does not replace the saved choice.

Use `/trust` to grant repository capabilities through the existing trust selector. Trust and mutation approval are separate: permitting source mutation does not itself approve automatic diffs. The existing trust command remembers upgrades; to lower remembered trust, change the local permission file and reopen the repository. Do not rely on selecting a lower trust level to revoke an existing higher grant.

Ordinary source commits and restarts do not invalidate a saved choice. Another checkout, even with the same folder name or checked-in settings, has a separate local identity. Moving a checkout requires a new choice. Headless operation never supplies consent on the user's behalf.

## Location and format

The host derives the file location from the same user directory as `config.json`:

```text
~/.threadsmith/repositories/<readable-name>-<canonical-path-sha256>/permissions.json
```

On Windows, `~` normally means `%USERPROFILE%`. The path hash uses the normalized absolute checkout path and filesystem case behavior. The document also records and verifies that identity. Do not rename a folder or copy another repository's document to transfer authority. Linked permission directories are rejected.

The host creates an envelope like this; the identity and hash are host-generated, not values to copy from this example:

```json
{
  "schemaVersion": 1,
  "repositoryIdentity": "<host-generated absolute repository identity>",
  "revision": 1,
  "settings": {
    "repository": { "trust": "TrustedMutation" },
    "mutation": { "approvalPolicy": "AlwaysTrustRepo" }
  }
}
```

Only declared permission settings are allowed in `settings`. Keep model selection, tool enabled/disabled preferences, themes and other project preferences in their existing locations. `/tools` still uses the common enablement path; permission grants do not automatically enable optional tools. `tools.allow: []` still denies all tools; omitting it inherits the normal filter. No migration removes an empty allow-list.

The host also records `repository.trustGrantedAt` with a trust choice. Updating solution discovery facts does not rewrite that choice or its timestamp. Only an explicit trust change or the validated one-time legacy import writes these permission values.

## Defaults and enforced restrictions

For versioned configuration, ordinary permission defaults resolve in this order: machine configuration, user configuration, user-owned repository permissions, then supported `THREADSMITH_` launch settings. Arrays replace lower arrays, including an explicit empty array. Source validation happens before merging. Repository configuration, repository session files and `--set` cannot grant permission. `repository.trust` is supported only in the user-owned repository file and the existing trust command.

`configurationOwnershipVersion: 1` in user or machine configuration opts that file into default semantics. Do not add it merely to suppress a warning: review existing restrictions first. New first-run user scaffolding uses this version and omits permission examples instead of recording compiled defaults as explicit restrictions.

Machine administrators can place restrictions in an explicit `enforced` object:

```json
{
  "configurationOwnershipVersion": 1,
  "enforced": {
    "mutation": { "approvalPolicy": "ReviewAll" },
    "tools": { "deny": ["run_process"] }
  }
}
```

User, repository and launch sources cannot define `enforced`. Restrictions narrow operation policy; they do not become grants. Existing adapter-owned settings retain their subsystem validation. Startup-bound provider, hook, MCP and runtime settings require a restart when changed. Other policy changes apply through the existing reopen/rebind lifecycle, which invalidates stale operation authority. Editing a file does not promise an instantaneous change to an already admitted OS process.

## Existing installations

Unversioned user restrictions and legacy repository restrictions are preserved until a deliberate local migration decision. `/policy` offers **Preserve existing restrictions** or **Use user defaults and user-owned repository permissions**, plus Cancel. The choice is recorded once as `legacyPolicyMigration` in the local envelope; no global configuration is rewritten. Reopen the repository to apply it. The second choice treats old user settings as defaults and stops applying permission settings from repository files. Explicit machine enforcement remains in force. This decision is separate from the selected mutation mode.

Previously saved host-owned SQLite trust and mutation choices can be imported once into a missing permission document. Repository configuration is never an import source. An existing local document wins as a whole: missing fields are not silently filled with older grants. Legacy database rows are retained, while a completion marker prevents deleting the new file from resurrecting them. SQLite remains the discovery-facts store, not a second active permission source.

Malformed documents, identity mismatches, unsupported versions, inaccessible storage and failed writes do not grant authority. A failed mutation-choice save leaves Review All active in that process and reports that persistence failed. Correct the file/store problem and retry. Coordinated updates preserve unrelated fields and use temporary-file publication with an expected-content check; concurrent external writers can still race between checking and replacement. Avoid editing the file in another application while changing settings in Threadsmith.

## Boundaries retained

Ordinary repository mutations are repository-wide, independently of the legacy `editableRoots: ["src/"]` template. Protected configuration, policy, secrets, Git metadata, prohibited paths, child assignments and normal action approvals still apply. The entire user-owned permission directory, including future files and containing-directory replacements, is protected from generic model file mutations.

Scratchpad selection is ordinary configuration, not a separate permission setting. Any valid configured path designates its directory for scratchpad access and complete lifecycle cleanup, without a second grant or special folder name. Repository/session selections remain confined to the repository; other sources retain existing external-directory support. Null disables the feature. Link, containment and protected-path checks still apply. This grants only the existing scratchpad capability, not general external-folder access.

Existing `tools.writeFile.allowedFolders` remains a write-only export grant. This change adds no external-directory read/edit feature and has no cleanup lifecycle. Specialized MCP identity approval, skill trust/revocation and exact outbound consent retain their existing stores and validators; those records are not interchangeable with configuration defaults.

This is application authorization, not an OS sandbox. An admitted build, shell, extension or local server can exercise the user's ambient OS permissions.

## Adding a permission setting

Declare it on the owning property in `PermissionConfiguration` using `ConfigurationPermissionAttribute` and its JSON name. Choose the accepted sources, input shape and, where applicable, resource default or startup binding. `ConfigurationPermissionCatalog` caches these declarations for the common loader, settings guard and permission writer. Do not add a second string list at a consumer.

Use `ConfigurationDocument` for bounded, case-insensitive source parsing and `RepositorySettingsCoordinator` for coordinated publication. Permission edits route through `RepositoryPermissionStore`; ordinary preferences retain their existing coordinator callers. Runtime authorization remains in the existing policy services. For structured arrays or dictionaries, annotate the whole permission-bearing property and retain the subsystem's bounded value validator.

Add disposable tests through the real loader/writer for every admitted and rejected source, absent/null/empty values, aliases, defaults versus enforcement, save/reload, protected descendants and the operation that consumes the setting. Test fixtures must not launch a host that can migrate personal state. A passing Windows test does not establish macOS/Linux filesystem behavior or installed-build behavior.
