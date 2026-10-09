# Tool Runtime Operations

The built-in tool registry includes:

| Tool | Minimum trust | Approval | Purpose |
|---|---|---|---|
| `list_files` | `UntrustedInspection` | None | Bounded repository file inventory. |
| `read_file` | `TrustedRead` | None | Bounded file range read (default input-file limit 1 MiB). |
| `memories` | `TrustedRead` | None | Explicit add/update/remove/list for local repository notes; bounded complete-input embeddings and best-effort recall. |
| `repository_intelligence` | `UntrustedInspection` | None | Read-only controls and explicit bounded structural profiles. Source reads retain their own trust/policy requirements. Parent conversation only. |
| `edit_source` | `TrustedMutation` | Exact mutation policy/review | Ordered source changes with exact anchors, shared transactional writes, durable receipts and advisory compiler feedback. Parent conversation only. |
| `write_file` | `TrustedRead` | None | Direct UTF-8 report/data output inside `tools.writeFile.allowedFolders` (default `.inbox`); default content limit 1 MiB. |
| `search` | `TrustedRead` | None | Bounded plain-text or timeout-limited regular-expression search. Installed releases use their RID-matched bundled `tools/rg(.exe)` for whole-repository literal searches; source-development launches may resolve `rg` from `PATH`. The fast path respects repository ignore files and falls back to the confined managed scanner when unavailable or when host pre-read path filtering is required. Generated/Git/reparse-point subtrees, SQLite databases, oversized files, and files that become locked or inaccessible are excluded as applicable. Use semantic tools for declarations, references, and implementations. |
| `git_status` | `TrustedRead` | None | Bounded Git branch and working-tree status. |
| `find_symbol` | `TrustedBuild` | None | Compiler-backed symbol declarations. |
| `code_explore` | `TrustedBuild` | None | C# exploration from a natural-language question, exact symbol, file, or code term, returning relevant dependencies and grouped current source. |
| `find_references` | `TrustedRead` | None | Compiler references, or explicit `TextOnly` fallback. |
| `find_implementations` | `TrustedBuild` | None | Compiler-backed implementations. |
| `run_process` | `TrustedBuild` | User | Allow-listed non-interactive process execution. |
| `jira` | `UntrustedInspection` | Outbound consent | Read one configured Jira Cloud issue description by key or supported browse URL. Disabled by default. |
| `invoke_skill` | `TrustedRead` | None | Invoke one explicit verified/enabled/compatible declarative package through the governed workflow boundary. |

`code_explore` intentionally uses a minimal model contract: required `query` plus optional `maxFiles` and `concepts` applicability hints. The query may be a natural-language architecture or behavior question, an exact C# symbol, a repository-relative `.cs` path, focused code terms, or a host-issued `code_explore:continue:...` retry cursor copied from an earlier Markdown follow-up target. The repository is selected by the host. Traversal depth, graph sizes, source and artifact character/byte budgets, timeouts, exact internal anchors, mode/emphasis, and continuation identity remain host-owned and are never exposed as model arguments. `maxFiles` is only a hint and is clamped to host limits without a corrective model turn. For dependency, caller, affected-project, or test-impact wording, the adapter derives the internal impact emphasis from `query`; users and models do not pass a separate `mode` field.

Exact source continuation cursors use configured source allowances without repository-tier reductions, while retaining per-file, total, output, and model-capacity limits. They skip relationship traversal and associated-artifact discovery and omit repeated selected-evidence presentation. Follow a continuation only when the missing evidence is needed. Digests are retained whenever the file has already been emitted; a target omitted before reading may not yet have a digest. Exact emitted ranges from partial results can become visible-source back-references; omitted lines never count as read.

`list_files.maximumEntries`, `search.maximumMatches`, and `run_process.timeoutSeconds` are nonnegative hints. Zero uses the configured default. Positive values above the configured ceiling run at that ceiling without a corrective model turn; negative, noninteger, and overflowing values remain invalid arguments.

Native model contracts keep host bookkeeping out of model input. `test_run_targeted.testId` is the string returned by discovery, and `diagnostic_query.runId` is an optional UUID string. `git_diff` and `git_show` expose only the optional `paths` array for path filtering; their adapters reuse the existing scalar internal request for one path. `csharp_script.kind` is the closed `expression` or `statement` string and defaults to `expression`.

The default model-visible result is concise Markdown: an exploration heading, relevant symbol/file count, compact selected-evidence rationale, grouped line-numbered source, blast-radius or call-flow evidence only when relevant to the question, associated artifacts when useful, bounded artifact completeness/omission notes, and focused follow-up targets or omissions. Blast-radius Markdown reports bounded-analysis totals and a balanced sample of callers, implementations, projects, and tests. Before terminal model bounding, the structured result preserves host totals and omission state; progressive bounding may remove individual detailed items. Automatic artifact discovery keeps source-proven relationships but admits project-wide additional documents, analyzer configuration, and project metadata only for explicit artifact/configuration focus. Follow-up targets include a small kind-diverse set of pasteable retry query cursors that preserve host-owned source, artifact, or impact continuation identity; additional targets are summarized by count. There is no separate cursor argument. The structured result and rendered Markdown share the selected-model byte ceiling; an explicit output note replaces any final Markdown tail that cannot fit. At the 1 KiB terminal envelope, the structured projection may retain only workspace generation, confidence, conservative incomplete coverage, and, when it fits, a bounded top-level omission. Candidate-ranking tables, adaptive-budget details, allocation accounting, digests, workspace generations, and other audit metadata remain in the authoritative structured result only while they fit instead of dominating the model context. Operators may temporarily select structured output for diagnostics, but Markdown is the default.

Internally, each request still captures one semantic workspace generation, ranks compiler-known declarations deterministically over policy-admitted paths, enforces trust/path/source/artifact limits, preserves current-source identity, and returns exact structured continuations and back-references. A pasted retry cursor is decoded only by the host. Source and artifact replays validate their encoded path, range, generation, optional digest, and current invocation policy; semantic continuations replay their encoded symbol anchor. Decoded stale, drifted, or policy-denied cursors fail closed, while text that is not a valid cursor remains an ordinary query. Artifact content remains untrusted data and is never executed, imported, used as C# authority, or recursively mined.

See [`docs/architecture/code-explore-tool.md`](../architecture/code-explore-tool.md) for the declaration catalog, retrieval, ranking, graph, source-allocation, continuation, and output-fitting implementation.

`invoke_skill` is a host adapter, not a package capability grant. It accepts only an explicit selector plus JSON input during the evidence-collection boundary; package verification, enablement, input schema, trust/tool/model/host compatibility, budgets, current-step loading, and every nested central tool call remain enforced. A returned skill host action is a proposal, not an applied effect. See [governed skill operations](skills.md).

Repository configuration can narrow tools with `tools:allow` and `tools:deny`, and can raise approval with `tools:requireApproval`. `tools:allowedExecutables` and `tools:allowedNetworkHosts` are explicit allowlists. Paths must remain below the repository and an approved root, must not match `prohibitedPaths`, and cannot traverse symbolic links or junctions. Recursive listing, search, and source-bearing code exploration also filter prohibited descendants and reparse points.

User-file `tools.allowedNetworkHosts` entries also authorize credential-free public HTTPS fetches on those exact hostnames when `web_fetch` is enabled and consented. The URL approval prompt can add an entry immediately; other configuration layers do not mint this fetch authority. See [web-fetch approval durations](web-fetch.md).

Secret-consuming tools declare exact logical `secrets:` references to host policy for each invocation; values are resolved only at the final privileged boundary and are not persisted. `web_search` always claims its configured Brave reference, `jira` claims only the selected account's token reference, `nuget_health` claims private-source references only in configured-source mode, imported MCP tools claim their profile's complete `secretScope`, and extension tools may return capability-defined claims. Enabled tools may use their configured credentials without a separate secret allowlist; policy validates declared reference syntax and the resolver enforces source trust and store safety. `ToolDefinition` and `/tools` do not currently project a static secret-readiness flag or exact names, so enabled/advertised does not guarantee that a required credential is present and resolvable. See [static secret discovery](secret-discovery.md) for sources, trust levels, and setup. Jira account setup and scoped/unscoped endpoint selection are documented in [Jira issue reads](jira.md).

Tool activity, success/failure, truncation, a 4,096-character sanitized result preview, and pending approvals appear in host projections and the TUI conversation/headless surfaces. Full bounded results are stored in `ToolInvocationCompleted`; raw arguments are not stored in activity events. Failures are normalized as invalid arguments, policy denial, approval denial, timeout, cancellation, execution failure, or output-limit failure.

`/intelligence status` in interactive or headless use, an authorized internal status command, and a model call to `repository_intelligence` enter this same tool pipeline. The status action has empty arguments (`{}`) and reports the active checkout's four controls plus `analysisAvailable: false`; it does not scan files, query a model, or create intelligence storage for an all-off checkout. The tool must remain enabled and pass the usual trust, allow/deny, approval, budget, cancellation, and output checks. Child agents cannot invoke it even by naming the tool directly. A failed host status command returns an invocation ID, failure kind, and sanitized error; the corresponding ordinary start/completion activity shows whether policy denied it, it failed, timed out, or was cancelled. A checkout mismatch is rejected before tool submission.


### Explicit deterministic repository profile

An explicit `repository_intelligence` tool call can include `profile`. This is an invocation-only structural capture; it does not enable any control, create canonical intelligence, call an inference provider, evaluate MSBuild, run tests or execute repository scripts. `analysisAvailable: false` continues to refer to semantic analysis/onboarding, not this deterministic operation. `/intelligence status` and `/intelligence preview` remain status/preview commands; there is no separate `/intelligence profile` command. The source reads require `TrustedRead`, the relevant built-in tools enabled and permitted, and `git` on the executable allowlist.

Example tool arguments:

```json
{
  "profile": {
    "revision": "HEAD",
    "paths": ["src", "Directory.Build.props"],
    "maximumPaths": 200,
    "maximumScannedPaths": 10000,
    "maximumFiles": 32,
    "maximumBytes": 65536,
    "maximumSeconds": 30,
    "includeOverlay": false
  }
}
```

Empty `paths` selects the repository root. At most eight literal scopes are accepted; glob syntax is not interpreted. The maximum settings are 200 returned discovery paths, 10,000 scanned records per metadata-discovery/listing pass, 32 admitted content files (also capped by trusted `repositoryIntelligence:limits:maximumFiles`), 65,536 admitted content bytes, and 60 seconds. Defaults match the example except that `paths` is empty. Inventory prioritizes project/build metadata before its ordinary path page. Text bodies are admitted from inventory sizes, using configured per-file and batch ceilings (defaults: 16 KiB and eight files). Metadata acquisition has a separate configured allowance; scan/page/policy/byte limits appear as omissions. These are bounded samples rather than exhaustive repository maps.

`snapshot` separates the hashed local common-Git-directory identity from the checkout identity, selected ref, resolved immutable commit, observed HEAD, branch and shallow-history limitation. Linked worktrees share the common identity but retain distinct checkout identities and controls; copies retain separate identities. Non-Git roots and unborn HEAD expose an absent commit instead of inventing a baseline. Every committed fact retains a `commit:objectId` source identity and path. A final snapshot exposes changed HEAD/branch/identity as `pendingChanges`; an unverified final state is also pending. Selecting a historical ref does not substitute the current semantic workspace for that revision.

`includeOverlay` requests separate mutable observations. Git change metadata includes additions, edits, deletions and rename source/destination paths within the requested scope; union clipping is explicit. At most three mutable files are read twice through `read_file`. Matching complete, unsanitized digests identify the observed bytes, not an atomic checkout-wide overlay. Unstable or unavailable reads remain qualified observations and produce no stable facts. Mutable facts use `overlay:<digest>` identities. Without a commit, the requested overlay uses bounded `list_files` discovery over at most three selected scopes and the same mutable reader. Without `includeOverlay`, mutable content is not substituted for an unavailable committed baseline.

Facts describe static declarations in .NET project/build XML, such as references, target frameworks and package declarations. Imports, conditions and property expressions are not evaluated; other admitted metadata/document files are identified without semantic interpretation. A leading UTF-8 BOM is omitted only from XML parsing input after the original content digest is verified. DTDs are rejected. Historical and immutable semantic analysis remain explicitly unavailable. `discoveredFiles`, `inspectedFiles`, `admittedFiles` and structured `omissions` distinguish discovery from successful interpretation and attempted/admitted work. `admittedBytes` includes conservative reservations for repeated mutable reads; it is not an I/O performance measurement. At most 512 facts and 64 declarations per file are retained. The final profile is also clipped before serialization and after sanitization to the effective tool-output ceiling, with `OutputByteLimit` recorded while retaining snapshot/scope. An output limit too small for that required envelope fails normally.

The result is retained only through ordinary host tool/session retention. There is no feature checkpoint or durable profile store. Cancellation and revocation propagate through the existing admission and nested-tool boundaries. Initial snapshot failures cannot return a pinned profile; a later collection deadline can return completed facts with `DeadlineReached` and an unverified final state. Other required-read/policy failures use normal tool failure receipts. Physical file-change races and performance vary by filesystem; the overlay does not claim to freeze mutable files.

### Evidence collection limits

The internal evidence collector builds invocation-only packets from an explicitly captured profile. It has no production tool/action of its own and does not enable analysis, onboarding or automatic enrichment. Its [source, episode and coverage contract](../architecture/context-policy.md#bounded-repository-evidence) distinguishes current-snapshot collection from optional bounded history. Both use the existing nested Git/file tools, policy, sanitization and event correlation.

Trusted machine/user configuration under `repositoryIntelligence:limits:evidence` supplies the following ceilings. Repository configuration cannot enlarge this authority. Requests provide explicit cumulative budgets within these ceilings. Values are positive integers, except `packetReserveBytes`, which can be zero; unknown keys are rejected when the optional feature is resolved. Defaults are declared in `RepositoryEvidenceResourceLimits`.

| Setting | Default | Meaning |
|---|---:|---|
| `maximumInputBytes` | 262144 | Cumulative source and metadata acquisitions, including the prerequisite profile |
| `maximumPacketBytes` | 131072 | Serialized UTF-8 bytes in one packet, including JSON escaping and provenance |
| `maximumOutputBytes` | 1048576 | Cumulative serialized bytes across packets and inspections |
| `maximumProfileMetadataBytes` | 65536 | Prerequisite discovery metadata allowance, separate from profile bodies |
| `maximumMetadataReadBytes` | 16384 | Metadata acquisition per governed read |
| `maximumFileBytes` | 16384 | Source bytes acquired per file, also applied during profiling |
| `fileBatchSize` | 8 | Files requested per bounded batch |
| `historyPageSize` | 4 | Commits requested per bounded frontier page |
| `maximumChangesPerCommit` | 8 | Changed paths selected per commit |
| `maximumQuestionCharacters` | 2048 | Question characters before normalization |
| `maximumScopePaths` | 8 | Literal scope paths |
| `maximumSymbols` | 8 | Requested symbol anchors |
| `maximumSymbolCharacters` | 256 | Characters per symbol anchor |
| `maximumLocatorCharacters` | 2048 | Characters per source locator |
| `maximumInspectionLines` | 201 | Lines in one cached-source inspection |
| `maximumEvidenceIdentifiers` | 512 | Evidence IDs retained in one live operation |
| `packetReserveBytes` | 8192 | Space reserved for episode and omission metadata before content acquisition |
| `maximumDiagnosticLocatorCharacters` | 256 | Characters in an optional omission locator |

Git's existing trusted operational configuration uses `limits:git:maximumMetadataBytes` (default 65536) and `limits:git:maximumHistoryOffset` (default 64). These bound explicit metadata allowances and history cursors; the configured `maximumCapturedCharacters` remains an additional capture ceiling. A request cannot enlarge it. Raw metadata acquisition includes the reserved lookahead byte. A small allowance that cannot hold complete ancestry returns explicit truncation rather than classifying valid ancestry as malformed. Metadata-only diff reads collect changed paths and binary status without patches. Mutable `list_files` discovery can also receive a serialized metadata allowance and stops before acquiring a record that cannot fit.

### Opt-in live evidence verification

From a source checkout, the following test exercises the internal collector through a test-only adapter and the normal composed host. It uses synthetic temporary repositories, existing Threadsmith `openai-codex` authentication/catalog data, and exactly GPT-6.1-Sol at low reasoning; unavailable selection or fallback fails the check. No production evidence action is registered by this test.

```powershell
dotnet build tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj
$env:THREADSMITH_LIVE_REPOSITORY_EVIDENCE = '1'
$env:THREADSMITH_LIVE_REPOSITORY_EVIDENCE_REPORT_DIRECTORY = Join-Path $env:TEMP 'threadsmith-evidence-live-reports'
dotnet run --no-build --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj -- --filter-method '*RepositoryEvidenceRealModelConsumesSnapshotHistoryAndOverlayAsync' --progress off
```

The live case checks snapshot/history/overlay provenance, bounded continuations and inspections, unchanged controls, normal nested tool activity, evidence-ID citation, and both small-budget Git regressions. Reports contain packets, sanitized host events and visible responses. Inspect response claims against the reported evidence; mechanical success does not prove arbitrary model interpretation correct. Nested host reads still run when the model makes only one direct adapter call. Provider usage can be consumed; the normal offline suite skips this test unless explicitly enabled.

### Invocation boundaries

Host-submitted nested reads retain the parent invocation ID on start and completion events. They use distinct invocation IDs and consume one ordinary tool budget charge per admitted operation. The bridge permits at most two nested levels and 16 descendant submissions per root; it rejects a child that would wait on a source permit held by any ancestor. Parent cancellation reaches children. A rejected nested admission is reflected in its parent's ordinary failure, while admitted child work has its own terminal outcome. Explicit structural profiling uses these same nested reads.

Model-authored sibling tool calls are preflighted as one batch before any tool starts. If one sibling is malformed, unavailable, repeated, phase-invalid, or fails argument/schema validation before execution, Threadsmith rejects the entire batch, returns bounded corrective feedback for every correlated call, and asks the model to retry within `execution:maxCorrectiveTurns`. No valid sibling in that rejected batch is executed or retained as evidence.

Before validating model-authored JSON against an input or output contract, Threadsmith uses the common JSON cleanup path to extract a complete object or array from surrounding prose or Markdown fences. The extracted value still undergoes the original schema, type, policy, and authority checks; cleanup does not accept invalid fields or execute anything by itself.

Process executable values must be bare names; path-qualified values are rejected even when their basename is allow-listed. The process manager resolves the name only from absolute host `PATH` entries, never from the repository working directory. Cancellation terminates the entire tracked process tree, and a manager timeout is recorded as a failed tool invocation. Standard output and error are captured separately, sanitized, and bounded. If a semantic tool is unavailable, inspect the displayed semantic confidence and reopen the solution with `TrustedBuild` or explicitly request the supported text fallback.

## Availability and live activity

`/tools` uses a keyboard-only checkbox tree in TUIKit. Space applies each change immediately, including filtered groups of eligible tools, and leaves the dialog open. Esc retains completed changes. Entries marked `[locked]` are essential tools protected by the host’s tool definition/availability rules; the checkbox cannot disable them. Ordinary trust, approval, and scope checks still apply when they execute.

While a tool runs, its owner’s output pane shows a live block with an independent timer: `TOOLS:` for built-in and extension tools, `MCP:` for imported MCP tools. Completion replaces the transient entry with one retained outcome block. Concurrent calls keep their own identities and timers; final blocks may follow completion order while model results retain original request order. `tui:showOperationDurations=false` hides elapsed text without hiding operation state. `delegate_agents` adds current named child statuses to the same block. The `memories` tool reports its operation, outcome, memory type, and note text. See [workspace presentation](agent-workspace.md).

## Code exploration operational settings

`tools:codeExplore` supplies one restart-scoped snapshot to main and child calls. Positive numeric caps are enforced, zero disables them, and malformed negative values fail startup. Set `adaptiveSizingEnabled=false` to remove tier reductions, or `enforceOperationalLimits=false` to disable this tool's operational caps. Model capacity, cancellation, trust, paths, source identity and sanitization still apply. Configure source/artifact limits under `limits`, and output caps with `maximumResultBytes` and `maximumMarkdownBytes`; raising one does not implicitly change another. Both output boundaries use the effective limits after sanitization. Small, back-referenced and unavailable source sections release reservations and source-bearing slots for relevant remaining source. See the [configuration inventory](../architecture/code-explore-tool.md#operational-configuration-and-consumers) and [user guide](../user-guide.md#code_explore-task-sufficient-c-exploration).

### Saving reports and data with `write_file`

`write_file` creates text artifacts directly during a conversation. It avoids change planning, mutation proposals, builds, and tests. For an existing answer, use `{"path":".inbox/report.md","useLastResponse":true}`: the host copies the latest archived assistant answer from the current session exactly. Missing/removed answer bodies produce an error instead of a substituted report. For new content, supply `content` instead of `useLastResponse`.

Configure the folder list in machine, user, or repository `config.json`:

```json
{
  "tools": {
    "writeFile": {
      "allowedFolders": [".inbox", "reports", "C:/Reports"]
    }
  }
}
```

The default list is `[".inbox"]`. Higher-precedence lists replace lower lists completely; `[]` or `null` permits no writes. Relative entries resolve under the active repository; outside folders must be absolute. Entries are literal folders, include descendants, and do not accept globs or relative `..` escapes. Absolute paths may identify folders elsewhere on the machine. Repository settings can grant these destinations as requested by the operator; an allowlisted folder is direct file-write authority. Restart after configuration edits. Opening another repository rebinds its folder grants without inheriting the former repository's overrides.

Parent folders are created as needed. Files use UTF-8 without a BOM and preserve supplied text and line endings. Existing files are preserved unless the call explicitly sets `overwrite:true`; replacement publishes a completed sibling temporary file. Supported extensions are `.txt`, `.md`, `.markdown`, `.json`, `.csv`, `.tsv`, `.yaml`, `.yml`, `.xml`, `.log`, and `.rst`. Both content modes have a 1 MiB UTF-8 limit. Source/project changes retain the mutation workflow. Git metadata, `.threadsmith` settings, `AGENTS.md`, prohibited paths, and symlink/junction traversal are rejected even under an allowed folder. Ordinary read tools do not inherit access to external write folders.

The tool remains subject to repository trust, `/tools` availability, `tools.allow`/`deny`/`requireApproval`, normal tool audit, and cancellation. If an existing configuration has a nonempty `tools.allow` or explicit `tools.enabled` list, add `write_file`; remove any legacy placeholder denial of that name. It has a file-write side effect, serializes with conflicting work, and is excluded from read-only delegated-agent tool sets. Tool activity shows the destination; successful results report path and byte count without echoing the saved report.

## Repository memories

The single `memories` tool accepts `action` (`add`, `update`, `remove`, `list`) and optional nullable `id`, `text`, `memoryType`, `kind`, `concepts`, `expectedRevision`, and `confirmDistinctFrom`. Add requires text; update requires id/text; remove requires id only; list accepts no write fields. Add/update may set memoryType, kind, and concepts; expectedRevision belongs only to update and confirmDistinctFrom only to add. Unknown fields or invalid combinations return corrective feedback. The model cannot select repository paths, SQL, vectors, origin, timestamps, or the embedding provider. The deployed `Tool-memories-Description.md` guides concise durable preferences/corrections/project context and excludes routine progress, receipts, secrets, and instructions copied from untrusted sources.

This is a repository-scoped application-state write capability, serialized for conflicting calls and unavailable to read-only children. It needs no plan or code-mutation preview. Ordinary tool trust/allow/deny/enable controls remain effective; withholding this tool also withholds automatic memory injection, while explicit user `/memory` management remains available. Console activity identifies the operation and ID. Completed interactive tool blocks also show the actual outcome and full returned memory text, including removed notes; lists show each returned note and any omission count. Failed add/update attempts show the sanitized requested text. Memory bodies have a separate 98,304-character display bound, so the ordinary 240-character tool-detail truncation does not shorten typical notes. Headless tool results include the action, outcome, and returned note text in their bounded JSON preview.

The service sanitizes/bounds complete new text to the configured maximumTextCharacters (default 2,000 characters) and the encoder's 256-token limit; failed or truncated embeddings cannot change content or evict notes. Duplicates return an existing ID without renewing recency; updates preserve identity and reset usage; remove deletes search/usage state and is idempotent. List output is bounded and reports omitted entries, with manual `inspect` for an individual ID. Configure `tools:config:memories:MaxNumberOfRepoMemories` (20), `Recall:MaximumResults` (3), and `Recall:SemanticMinimum` (0.47) through normal machine, user, repository, session, CLI, and `THREADSMITH_` environment layering. The semantic setting is a finite double in `[-1, 1]` and admits only scores strictly greater than it; lexical retrieval is unaffected. Restart Threadsmith or reopen the repository after editing a configuration file. See [repository-memory operations](conversation-context.md#repository-scoped-memory) for context selection, sensitivity, eviction, migration, diagnostics, and fallback.

When reconciliation is enabled, an add can return `reconciliationRequired` with `Added=false`, candidate IDs/revisions/text, omission counts, and resolution guidance. Nothing is written or evicted. The model decides whether to call `update` with the selected ID and `expectedRevision`, or retry `add` with `confirmDistinctFrom: [{"id":"…","revision":1}]` for the currently observed collisions. A changed or newly discovered candidate requires a fresh decision. Superseding updates the existing ID; it does not leave another active copy. The host never interprets a similar note as permission to overwrite it.

Memory calls may repeat across tool rounds so a subsequent list observes intervening additions, updates, or removals. Identical calls within one response batch remain invalid. Repeated writes still pass ordinary validation, reconciliation, and revision checks.

Eligible built-in read/search/semantic/validation/process tools accept optional `concepts` as applicability hints. The central pipeline validates and normalizes them, then forwards them internally after admission; denied/invalid calls contribute nothing. Hints do not change operational duplicate-call identity, tool output, permissions, or MCP/extension schemas. `memories.concepts` writes the note's metadata and can discover additional candidates during an enabled add reconciliation check. See [concept recall](conversation-context.md#reconciliation-and-concept-recall) for admission and lifecycle limits.

Tool/service output and runtime limits are configurable. See [Resource limits](resource-limits.md) for defaults, per-tool runtime overrides, source concurrency, and the independent limits that apply to a result.

`edit_source` applies ordered text/create/delete/move operations through `SourceEditApplication` and the existing transactional writer. Exact review supplies authorization when mutation policy requires it; there is no second generic approval. Compiler findings and incomplete coverage are advisory feedback, while invalid paths, instructions and stale source identities reject unsafe edits. Effect receipts feed ordinary model continuations. The model chooses build and test tool calls after resolving incremental compiler findings; response completion records disk effects without launching validation.
