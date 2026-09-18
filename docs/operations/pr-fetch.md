# Pull-request retrieval

`pr_fetch` reads a GitHub.com or Bitbucket Cloud PR's metadata and complete changed-file inventory, with optional provider diff content. It never checks out a branch, writes source files, posts comments or compares two branch-tip trees. The maintained [review skill](../code-review.md) uses inventory retrieval for its lead handoff, then lets specialists request diff pages only when their assigned review work needs patch evidence.

## Configure and enable

Merge account bindings into user configuration (`~/.threadsmith/config.json`) or machine configuration, then restart. Repository configuration can disable an account or narrow limits; it cannot replace the adapter, redirect a credential, add accounts, or enable an account disabled in trusted configuration.

```json
{
  "tools": {
    "prFetch": {
      "providers": {
        "public-github": { "type": "github", "enabled": true, "authentication": { "mode": "none" } },
        "work-github": {
          "type": "github", "enabled": true,
          "urlPatterns": ["https://github.com/*/*/pull/*"],
          "authentication": { "mode": "bearer", "secretReference": "secrets:github:review-token" }
        },
        "work-bitbucket": {
          "type": "bitbucketCloud", "enabled": true,
          "urlPatterns": ["https://bitbucket.org/*/*/pull-requests/*"],
          "authentication": { "mode": "basic", "username": "developer@example.org", "secretReference": "secrets:bitbucket:review-token" }
        }
      }
    }
  }
}
```

Store referenced values using the existing Secrets provider; do not put tokens in this JSON. GitHub supports public reads and bearer tokens with repository/PR read access. Bitbucket Cloud supports public reads, bearer access tokens, and API tokens using Basic authentication with your Atlassian email as username. App-password onboarding is not provided. See the authoritative [GitHub PR API](https://docs.github.com/en/rest/pulls/pulls) and [Bitbucket authentication documentation](https://developer.atlassian.com/cloud/bitbucket/rest/).

Enable `pr_fetch` using `/tools`, accepting its ordinary outbound consent. Allow `api.github.com` or `api.bitbucket.org` in normal network policy. Credentials and skill verification do not grant permission. With no enabled configured account, the tool is not registered; check user/machine configuration and restart if it is missing. Invalid adapters/authentication fail configuration validation. Enabled account IDs and hosts appear in the tool description; there is no provider-list tool. If multiple accounts match, the model asks which one to use.

## Calls and evidence

```json
{"url":"https://github.com/owner/repo/pull/123","kind":"inventory"}
```

`kind` is required and has two values. Use `"kind":"inventory"` for PR metadata or a complete list of added, modified, removed and renamed files. This mode retrieves all file-inventory pages, rechecks PR metadata and completes without requesting the provider diff. Use `"kind":"diff"` when the current task requires patch content or changed-line evidence. The maintained review skill uses `inventory` before delegation and instructs specialists to request `diff` pages only for assigned evidence.

The tool selects the unique enabled account whose configured `urlPatterns` match the canonical PR URL. These are routing configuration consumed by host code, not instructions exposed to the model. Patterns are case-insensitive whole-URL globs: `*` matches any characters, including slashes. Use HTTPS, the adapter's exact host and optional wildcards in the path; queries, fragments, credentials and other wildcard syntax are unsupported. The adapter validates the actual PR URL before matching; supported PR subviews and fragments normalize to the same canonical URL.

Omitted or empty `urlPatterns` use adapter defaults: `https://github.com/*/*/pull/*` or `https://bitbucket.org/*/*/pull-requests/*`. Custom patterns replace those defaults and belong in user/machine configuration; repository configuration cannot replace them. Changes require restart. Overlapping patterns do not establish priority: if multiple accounts match, the tool asks for an explicit `provider` ID. With no match, it reports a configuration/selection error. An explicit `provider` selects that enabled account regardless of its automatic routing patterns, but the adapter still validates the URL. In the example above, the public GitHub account overlaps the work account; remove it or narrow its patterns to avoid that ambiguity.

When a result has a non-null `cursor`, call `pr_fetch` exactly once with the same URL, the same `kind` and that returned cursor. Do not repeat the previous arguments. Continue this sequence until `deliveryComplete` is true and the cursor is null:

```json
{"url":"https://github.com/owner/repo/pull/123","kind":"inventory","cursor":"<returned cursor>"}
```

`provider` remains optional after automatic selection, or can be the account ID returned by the tool. Provider, canonical URL and `kind` form the acquisition identity; changing the kind creates a separate snapshot, and a cursor cannot cross kinds. Every result reports its `kind` and `isContinuation`; the latter is true when that invocation supplied a cursor, without echoing the incoming cursor value. The separate `cursor` output is the opaque value for the next call. Inventory pages contain metadata, files and a terminal `complete` page. Diff pages additionally contain bounded diff chunks. `acquisitionComplete` means all requested provider data was retrieved with matching PR metadata before and after; `deliveryComplete` and a null cursor mean this reader reached the last page. A cached first page can have acquisition complete but delivery incomplete. The PR description is sent on the first page only. Snapshot ID and commit identities accompany every page. Destination commit is the destination tip, not a claimed merge base. Identical calls in one model execution receive the shared corrective duplicate result. Separate parent, skill and subagent executions can make their own first call and reuse the operation cache; continuation calls are distinct because they carry the returned cursor.

Before completion, evidence is provisional. A moving PR, failed page, inconsistent file count or exceeded bound fails acquisition rather than substituting scope. Completion means the API response was acquired, not that every binary or omitted patch can be reviewed. File inventory limitations and raw diff markers remain coverage gaps. Metadata text has explicit truncation markers when bounded. The before/after check is not an atomic snapshot. GitHub file-list ceilings are detected against its reported changed-file count.

Pages are acquired on demand, streamed and retained in a bounded operation cache. Parent, equally authorized children and nested skills share that cache; every invocation still passes ordinary tool/network/disclosure policy and tool accounting. Cancelling one waiter does not cancel another reader. Owner cancellation or completion disposes state and joins pending transport. Manual skills own a scope when no caller scope exists; resume starts fresh acquisition. There is no disk or cross-session cache.

To clear and reacquire one PR, call the same tool with `refresh:true` and no cursor. This replaces its generation and invalidates old cursors; concurrent refresh callers join the active replacement. Discard or reassess earlier reviewer evidence. Failed refresh does not restore old evidence. An ordinary first-page call can retry a failed acquisition.

## Limits

All keys are under `tools.prFetch`; trusted settings are ceilings for repository overrides. Changes require restart.

| Key | Default | Meaning |
| --- | ---: | --- |
| `maximumResponseBytes` | 16777216 | Decoded bytes per API response or raw diff stream; zero disables the ceiling. |
| `maximumCacheBytes` | 33554432 | Retained UTF-8 serialized metadata/pages across PRs in one operation; zero disables the ceiling. Exhaustion fails without evicting a captured revision. |
| `maximumFilePages` | 200 | File-inventory API pages per acquisition; metadata, diff and bounded redirects are additional. Zero disables the ceiling. |
| `timeoutSeconds` | 120 | Active acquisition deadline per requested page and declared tool timeout; zero disables it. Idle model time between pages is excluded. |
| `pageCharacters` | 8192 | Diff chunk / file-identity grouping size, 256–16384. Page size cannot be disabled. |

Ordinary runtime overrides, budgets and cancellation still apply. Serialized tool output defaults to 256 KiB for escaped page content and metadata. Secrets resolve at the HTTP boundary with user-owned minimum trust. Redirects/pagination stay on the adapter's API authority and repository route; public-IP validation pins connections. Tokens are absent from cache keys, evidence and diagnostics.

## Extending providers

Implement `IPullRequestProvider` in `Threadsmith.Tools.PullRequests` and register it in `PrFetchComposition`. Reuse `PullRequestProvider` transport when its authentication and route semantics fit. Adapters own URL validation, fixed authorities, authentication, PR comparison semantics and pagination; the shared tool owns selection, cache and output. No new model tool, SDK, review coordinator or persistence store is needed. Enterprise/self-hosted products need validated adapters; substituting a hostname is not supported.

`ToolDefinition.AllowDuplicateInvocations` defaults false and remains available to tools that intentionally need repeated identical calls. `pr_fetch` uses the default duplicate protection. The shared `ToolCallHistory` policy serves conversations, children and native skills. Separate executions retain independent histories and can reuse the shared operation cache. This changes duplicate rejection only, never policy, scheduling or budgets.
