# Pull-request retrieval

`pr_fetch` reads a GitHub.com or Bitbucket Cloud PR's metadata and complete changed-file inventory, with optional provider diff content. It never checks out a branch, writes source files, posts comments or compares two branch-tip trees. The maintained [review skill](../code-review.md) retrieves the complete inventory and diff in one call in the lead review, then supplies that evidence to its specialists. The tool is parent-only and is omitted from subagent tool catalogs.

## Captured evidence for delegated children

Successful parent PR acquisitions are retained in the owning operation scope. Delegated children receive the completed snapshot ID automatically and can inspect its metadata, full changed-file inventory, and diff through `read_agent_evidence`. The reader supports one-based line ranges for selective inspection. It reads the captured result and makes no provider request; `pr_fetch` remains absent from child tool catalogs. Snapshots expire with the operation scope and refresh replaces the prior handoff.

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

`kind` is required and has two values. Use `"kind":"inventory"` for PR metadata or a complete list of added, modified, removed and renamed files. This mode retrieves all file-inventory pages, rechecks PR metadata and completes without requesting the provider diff. Use `"kind":"diff"` when the current task requires patch content or changed-line evidence. The maintained review skill calls `kind:"diff"` once before delegation; that call includes the inventory as well as the diff.

The tool selects the unique enabled account whose configured `urlPatterns` match the canonical PR URL. These are routing configuration consumed by host code, not instructions exposed to the model. Patterns are case-insensitive whole-URL globs: `*` matches any characters, including slashes. Use HTTPS, the adapter's exact host and optional wildcards in the path; queries, fragments, credentials and other wildcard syntax are unsupported. The adapter validates the actual PR URL before matching; supported PR subviews and fragments normalize to the same canonical URL.

Omitted or empty `urlPatterns` use adapter defaults: `https://github.com/*/*/pull/*` or `https://bitbucket.org/*/*/pull-requests/*`. Custom patterns replace those defaults and belong in user/machine configuration; repository configuration cannot replace them. Changes require restart. Overlapping patterns do not establish priority: if multiple accounts match, the tool asks for an explicit `provider` ID. With no match, it reports a configuration/selection error. An explicit `provider` selects that enabled account regardless of its automatic routing patterns, but the adapter still validates the URL. In the example above, the public GitHub account overlaps the work account; remove it or narrow its patterns to avoid that ambiguity.

Each successful call returns the entire requested evidence after the host follows all provider API pages and verifies that PR metadata still matches. There are no model-facing cursors, continuation calls, or delivery-pending states. The result includes metadata, snapshot identity, the complete file list, available diff, limitations, and cache/acquisition status. Destination commit is the destination tip, not a claimed merge base.

A moving PR, failed provider page, inconsistent file count, or exceeded configured acquisition bound fails the call rather than returning provisional evidence as complete. Binary files and provider-omitted patches remain explicit limitations. The before/after metadata check is not an atomic provider snapshot.

Acquisition is retained in the existing operation cache. Concurrent authorized readers share the acquisition; cancellation of one waiter does not cancel other readers. Owner cancellation or completion disposes state and joins pending transport. Delegated children receive the evidence through the ordinary governed handoff; the tool itself is omitted from their catalogs. There is no disk or cross-session cache.

Use `refresh:true` to explicitly replace the snapshot. Earlier evidence must then be discarded or reassessed. Failed acquisitions can be retried by a later call. Every invocation, including a cache hit, still passes ordinary tool and network policy.

## Limits

All keys are under `tools.prFetch`; trusted settings are ceilings for repository overrides. Changes require restart.

| Key | Default | Meaning |
| --- | ---: | --- |
| `maximumResponseBytes` | 16777216 | Decoded bytes per API response or raw diff stream; zero disables the ceiling. |
| `maximumCacheBytes` | 33554432 | Retained UTF-8 serialized metadata/pages across PRs in one operation; zero disables the ceiling. Exhaustion fails without evicting a captured revision. |
| `maximumFilePages` | 200 | File-inventory API pages per acquisition; metadata, diff and bounded redirects are additional. Zero disables the ceiling. |
| `timeoutSeconds` | 120 | Complete acquisition deadline and declared tool timeout; zero disables it. |
| `pageCharacters` | 8192 | Internal provider read/grouping size, 256–16384; the model receives the complete result regardless of this size. |

Ordinary runtime overrides, budgets and cancellation still apply. The tool has no separate small output cap; configured runtime overrides and the model's context capacity still apply. Secrets resolve at the HTTP boundary with user-owned minimum trust. Redirects/pagination stay on the adapter's API authority and repository route; public-IP validation pins connections. Tokens are absent from cache keys, evidence and diagnostics.

## Extending providers

Implement `IPullRequestProvider` in `Threadsmith.Tools.PullRequests` and register it in `PrFetchComposition`. Reuse `PullRequestProvider` transport when its authentication and route semantics fit. Adapters own URL validation, fixed authorities, authentication, PR comparison semantics and pagination; the shared tool owns selection, cache and output. No new model tool, SDK, review coordinator or persistence store is needed. Enterprise/self-hosted products need validated adapters; substituting a hostname is not supported.

`ToolDefinition.AllowDuplicateInvocations` defaults false and remains available to tools that intentionally need repeated identical calls. `pr_fetch` uses the default duplicate protection. The shared `ToolCallHistory` policy serves conversations, children and native skills. Separate executions retain independent histories and can reuse the shared operation cache. This changes duplicate rejection only, never policy, scheduling or budgets.
