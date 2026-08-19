# Static secret discovery

Threadsmith keeps logical references separate from values. Supported static-credential fields contain a `secrets:` reference; the host resolves it only when the owning component is about to authenticate a model, Brave search, MCP connection, trusted HTTP hook, or private NuGet source.

## Sources and precedence

For one eligible reference, Threadsmith tries these compiled sources in order:

1. the exact `THREADSMITH_` environment variable;
2. the active repository store at `<repo>/.threadsmith/secrets/config.json`;
3. the user store at `~/.threadsmith/secrets/config.json`.

The environment variable for `secrets:models:example` is `THREADSMITH_secrets__models__example`. The dedicated secret provider reads only that derived variable. Ordinary `THREADSMITH_` configuration loading excludes the complete normalized `secrets` subtree, so the raw value is not available through `IConfiguration`.

The user store is the recommended durable personal source. It is a separate strict-JSON file, not an `IConfiguration` layer:

```json
{
  "secrets": {
    "BRAVE_SEARCH_API_KEY": "replace-with-the-real-value",
    "models": {
      "example": "replace-with-the-real-value"
    }
  }
}
```

Each colon-delimited reference segment becomes one nested property below `secrets`. For example, `secrets:models:example` selects `secrets` → `models` → `example`. Empty strings are rejected and are safe placeholders until a value is supplied.

Create/edit this file outside Threadsmith and protect it as owner-only. On Windows, Threadsmith requires current-user ownership and rejects ACL entries that grant filesystem authority to other local principals; inherited access for Local System and built-in Administrators remains eligible. On Linux/macOS:

```powershell
New-Item -ItemType Directory -Force (Join-Path $HOME ".threadsmith/secrets") | Out-Null
chmod 700 (Join-Path $HOME ".threadsmith/secrets")
chmod 600 (Join-Path $HOME ".threadsmith/secrets/config.json")
```

On Windows, keep the file under the current user's profile and do not grant other users access. Threadsmith rejects unsafe group/other Unix modes. The initial release intentionally has no `/secrets set` command because accepting values as normal command arguments would expose them to shell history and process inspection.

## Consumer trust and policy claims

Provider precedence applies only among sources eligible for the consumer's minimum trust:

| Consumer | Static-secret behavior | Minimum source trust |
|---|---|---|
| OpenAI-compatible configured model | Optional provider `secretKeyReference` | `RepositoryOwned` |
| Brave `web_search` | Required API-key reference | `UserOwned` |
| MCP stdio | Every profile `secretScope` entry becomes one child environment variable | `UserOwned` |
| MCP HTTP/SSE | Header values beginning with `secrets:` and optional OAuth client secret | `UserOwned` |
| Managed HTTP lifecycle hook | Optional single bearer reference | `UserOwned` |
| `nuget_health` configured private source | Optional paired username/reference | `UserOwned` |

Native Codex and MCP OAuth access/refresh tokens remain in their specialized user-owned lifecycle caches and do not use this static store. Executable hooks reject secret bindings. `web_fetch`, local Git/semantic/build/test tools, `run_process`, `datetime`, and `csharp_script` do not receive resolver-supplied secrets.

At invocation time, secret-aware tools declare exact logical references through host-only policy claims. The host compares those claims with trusted `tools.allowedSecretReferences` before resolution. Exact names are not added to model-facing schemas. `/tools` currently shows availability and outbound consent, not secret readiness; a tool can therefore be enabled yet fail policy or resolution with a sanitized actionable error when its reference is not allowlisted or available.

## Repository store safety

A repository store is lower-trust and is used only by components that explicitly accept `RepositoryOwned` values. Brave authentication requires `UserOwned` trust, so an ignored repository value cannot supply or override its API key. Before reading it, Threadsmith requires all of the following:

- the canonical file remains under the active repository with no symbolic-link/junction/reparse escape;
- the opened target is a regular file; Unix opens are nonblocking, so a FIFO, socket, or device cannot stall resolution;
- Git can determine index and ignore state relative to the opened repository directory, including when that directory is below the worktree root;
- neither the exact file nor an applicable ancestor/store representation is tracked or staged for addition;
- an effective Git ignore rule covers the exact file.

Add this rule before creating the file:

```gitignore
.threadsmith/secrets/
```

If a secret file was ever added to the index, remove only the index entry, keep the local file, and verify it is ignored:

```powershell
git rm --cached -- .threadsmith/secrets/config.json
git check-ignore -v --no-index -- .threadsmith/secrets/config.json
git ls-files --error-unmatch -- .threadsmith/secrets/config.json
```

The final `git ls-files` command must report no match. Do not commit the removal automatically while using Threadsmith; review normal Git state yourself. Git ignore prevents accidental addition but does **not** encrypt the file, make the repository trusted, or erase it from existing history. Rotate any value that was committed.

## Failure behavior

Resolution fails closed for malformed references/stores, empty or non-string values, case-insensitive duplicate keys, excessive size/depth/count, non-regular files, unsafe paths/permissions, tracked or unignored repository files, unavailable/indeterminate Git state, missing eligible providers, source-trust rejection, bootstrap cycles, caller cancellation, and per-provider timeout. A timeout cancels the provider token; repository Git proofs wait asynchronously and terminate their process tree when cancelled. Errors contain the logical name, component, stable safe provider outcomes, and remediation—never the value, raw provider exception, file contents, or environment contents.

A plain string containing `secrets:example` does not trigger discovery. Provider catalogs and typed profiles retain the logical reference. Repository configuration cannot add/reorder providers, lower minimum trust, or use a repository value to bootstrap user/managed policy.

## OAuth compatibility

Static MCP OAuth client-secret references use the resolver. MCP and Codex access/refresh tokens remain in their lifecycle-specific user caches so refresh, logout, restoration, and permission behavior remain intact. Never copy OAuth caches into either static JSON store.

## Future providers

A future trusted adapter can implement `ISecretProvider` without changing consumers. Provider registration remains compiled and host-owned; repositories, models, tools, skills, extensions, MCP servers, and hooks cannot register one. Network providers must additionally enforce bounded transport, endpoint policy, cancellation, safe failures, SDK isolation, and non-cyclic authentication bootstrap.
