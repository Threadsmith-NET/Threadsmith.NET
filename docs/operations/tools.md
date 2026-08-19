# Tool Runtime Operations

The built-in read-only registry contains:

| Tool | Minimum trust | Approval | Purpose |
|---|---|---|---|
| `list_files` | `UntrustedInspection` | None | Bounded repository file inventory. |
| `read_file` | `TrustedRead` | None | Bounded file range read (files up to 1 MiB). |
| `search` | `TrustedRead` | None | Bounded plain-text or timeout-limited regular-expression search. Installed releases use their RID-matched bundled `tools/rg(.exe)` for whole-repository literal searches; source-development launches may resolve `rg` from `PATH`. The fast path respects repository ignore files and falls back to the confined managed scanner when unavailable or when host pre-read path filtering is required. Generated/Git/reparse-point subtrees, SQLite databases, oversized files, and files that become locked or inaccessible are excluded as applicable. Use semantic tools for declarations, references, and implementations. |
| `git_status` | `TrustedRead` | None | Bounded Git branch and working-tree status. |
| `find_symbol` | `TrustedBuild` | None | Compiler-backed symbol declarations. |
| `find_references` | `TrustedRead` | None | Compiler references, or explicit `TextOnly` fallback. |
| `find_implementations` | `TrustedBuild` | None | Compiler-backed implementations. |
| `run_process` | `TrustedBuild` | User | Allow-listed non-interactive process execution. |
| `invoke_skill` | `TrustedRead` | None | Invoke one explicit verified/enabled/compatible declarative package through the Plan-39 workflow boundary. |

`invoke_skill` is a host adapter, not a package capability grant. It accepts only an explicit selector plus JSON input during the evidence-collection boundary; package verification, enablement, input schema, trust/tool/model/host compatibility, budgets, current-step loading, and every nested central tool call remain enforced. A returned skill host action is a proposal, not an applied effect. See [governed skill operations](skills.md).

Repository configuration can narrow tools with `tools:allow` and `tools:deny`, and can raise approval with `tools:requireApproval`. `tools:allowedExecutables` and `tools:allowedNetworkHosts` are explicit allowlists. Paths must remain below the repository and an approved root, must not match `prohibitedPaths`, and cannot traverse symbolic links or junctions. Recursive listing and search also filter prohibited descendants and reparse points.

Secret-consuming tools declare exact logical `secrets:` references to host policy for each invocation; values are resolved only at the final privileged boundary and are not persisted. `web_search` always claims its configured Brave reference, `nuget_health` claims private-source references only in configured-source mode, imported MCP tools claim their profile's complete `secretScope`, and extension tools may return capability-defined claims. Trusted `tools.allowedSecretReferences` must contain every claim. `ToolDefinition` and `/tools` do not currently project a static secret-readiness flag or exact names, so enabled/advertised does not guarantee that a required reference is allowlisted and resolvable. See [static secret discovery](secret-discovery.md) for sources, trust levels, and setup.

Tool activity, success/failure, truncation, a 4,096-character sanitized result preview, and pending approvals appear in host projections and the TUI conversation/headless surfaces. Full bounded results are stored in `ToolInvocationCompleted`; raw arguments are not stored in activity events. Failures are normalized as invalid arguments, policy denial, approval denial, timeout, cancellation, execution failure, or output-limit failure.

Process executable values must be bare names; path-qualified values are rejected even when their basename is allow-listed. The process manager resolves the name only from absolute host `PATH` entries, never from the repository working directory. Cancellation terminates the entire tracked process tree, and a manager timeout is recorded as a failed tool invocation. Standard output and error are captured separately, sanitized, and bounded. If a semantic tool is unavailable, inspect the displayed semantic confidence and reopen the solution with `TrustedBuild` or explicitly request the supported text fallback.
