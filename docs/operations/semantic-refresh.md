# Semantic Refresh

Threadsmith keeps the selected Roslyn workspace current when relevant repository files change outside the host. One workspace-scoped coordinator owns automatic background refresh, request admission, Threadsmith-owned mutation refresh, recovery, and explicit manual refresh.

## Automatic refresh

The repository monitor treats filesystem notifications as untrusted hints. It normalizes and confines candidate paths beneath the active repository, excludes prohibited paths, `.git`, `bin`, `obj`, Threadsmith state, reparse points, and known temporary files, and coalesces duplicate editor notifications during a short bounded settling window. Monitoring uses bounded non-recursive directory scopes so excluded nested directories do not first consume a recursive operating-system notification buffer. Stable content identity removes timestamp-only and duplicate no-op notifications. A watcher error or notification-bound overflow rebuilds monitoring and triggers bounded recovery with a complete reload rather than declaring the workspace current.

An ordinary edit to an existing loaded C# document is eligible for incremental replacement. This includes an editor's atomic save when create/delete/rename hints settle to the same existing loaded path. The host reads the stable confined file, verifies its loaded document identity, builds one immutable replacement solution, and publishes one new semantic generation. Actual document membership changes, solution/project changes, build property/target changes, package, SDK, reference, analyzer/configuration, and uncertain changes use the existing complete-load path. A completed load with compiler diagnostics may publish reduced confidence; only infrastructure or currentness failure leaves the workspace dirty.

## External edits: refresh or ignore

Threadsmith applies these rules after it confines a notification to the active repository and lets the file settle:

| External edit or signal | Treatment |
| --- | --- |
| Content change or atomic replacement of an existing C# document already loaded in the selected workspace | Incremental refresh when the settled path still exists and its document identity and project membership are exact; otherwise complete refresh. |
| Content change to any other `.cs` file beneath the repository | Complete refresh, because wildcard project membership may have changed. |
| Change to a loaded additional document or analyzer-config document | Complete refresh. |
| Change to a solution or project file, `.props`, `.targets`, `.ruleset`, `.editorconfig`, `.globalconfig`, `global.json`, `nuget.config`, `Directory.Packages.props`, or `Directory.Build.*` | Complete refresh. |
| Change to another inventoried semantic input, such as an analyzer or reference | Complete refresh, except for normally excluded build-output paths described below. |
| Create, delete, or rename of any other non-excluded file or directory | Complete refresh. This is intentionally conservative because project and additional-file membership may be defined by wildcards. |
| Watcher error, overflow, or internal notification-bound overflow | Complete recovery refresh. |
| Another uncertain change whose stable content can be read | Complete refresh, retaining the original external-change or host-mutation attribution unless recovery was separately requested. |
| A file that does not remain stable long enough to read | No workspace publication. The refresh fails, the workspace remains dirty, and a later stable notification or `/semantic_refresh` can retry. |

The following notifications do not start a refresh:

- duplicate editor notifications, timestamp-only touches, and writes whose stable content identity has not changed;
- changes beneath `.git`, `.threadsmith`, `.codegraph`, `.idea`, `.inbox`, `.vs`, `.vscode`, `artifacts`, `bin`, `node_modules`, `obj`, or `TestResults`;
- known editor temporary files: names beginning or ending with `~`, plus `.tmp`, `.swp`, and `.swo` files;
- paths outside the active repository, configured prohibited paths, and paths reached through a reparse point or symbolic link;
- content changes to an existing non-C# file that is not a loaded additional/analyzer-config document, graph-control file, or another inventoried semantic input.

An explicitly loaded source, additional, or analyzer-config document takes precedence over the normal directory exclusions. Conversely, generated binaries and references under excluded output/tooling directories remain ignored. Otherwise-relevant, non-excluded Threadsmith-owned writes follow the same refresh rules; host attribution does not bypass the relevance filter. Their watcher echoes join the same refresh work and are coalesced so they do not produce a second external-change announcement.

Before the initial load, the startup consistency snapshot conservatively reads repository graph-control files and possible repository-local binary inputs. Each known generated, IDE, local-tool, package, test-result, or Threadsmith-state directory entry counts once toward traversal safety, but its contents are not enumerated and consume no scan-entry, path-count, or byte budget. Ignored temporary files likewise consume only their single traversal entry, not path-count or byte budget. Outside those exclusions, candidate inputs remain subject to the unchanged safety bounds and are reconciled with the exact semantic inputs reported by the loaded workspace. A later content-only edit to an uninventoried binary remains an unrelated-file no-op, while a create/delete/rename outside the excluded directories still follows the conservative lifecycle rule above.

For a successful externally attributed cycle, the interactive terminal prints exactly one start/completion pair:

```text
External changes detected; updating semantic model...
Semantic model updated (3 files, 240 ms).
```

Watcher-recovery cycles instead begin with `External changes require semantic recovery; updating semantic model...` and use the same single completion or actionable failure projection. Duration uses the ordinary operation-duration format. Reduced-confidence completion also names the resulting confidence. The serialized console owner writes these messages without clearing, submitting, or losing an active composer draft. Threadsmith-owned mutation echoes use the same coordinator but are not mislabeled as external and do not create duplicate mutation noise.

Semantic work does not wait for Threadsmith to regain focus or for the composer to submit. When the composer is empty, refresh lifecycle output cooperatively closes and immediately reopens that empty prompt, so the text appears without a keypress or other console interaction. Once the composer contains any draft text, including whitespace, lifecycle output remains queued behind PrettyPrompt until the draft is submitted, cancelled, or cleared back to empty; the draft is never submitted, cleared, or discarded by the refresh. Focusing the window alone is not a semantic trigger. If the user submits while refresh is active or its lifecycle text is pending, request admission still waits for the current semantic generation before creating a model run.

## Request admission

Every ordinary interactive or headless model request passes the same application freshness gate. If relevant changes are settling, refreshing, or awaiting controlled publication, submission waits for the shared work to converge before the host allocates a `RunId`, cancellation state, run budget, steering registration, conversation entry, model request, or tool activity. A change arriving during refresh advances the dirty version and causes a coalesced follow-up before waiters are released.

Failure does not advance the applied version. The submitted request fails with bounded actionable detail and never reaches the model. Local non-model commands remain available for inspection and recovery.

## Manual refresh

In the interactive terminal, run:

```text
/semantic_refresh
```

The command forces one complete refresh even when the workspace is clean, waits for controlled publication, and reports file count, duration, and resulting confidence. It is handled locally: it creates no model run, conversation message, budget, or tool call. If an incremental refresh is already active, the command joins it and requests one shared complete follow-up. Repeated manual callers share that follow-up. Cancelling one waiter does not cancel shared work; repository rebind and application shutdown obsolete the owning refresh safely.

Headless hosts call `HeadlessShell.ForceSemanticRefreshAsync(sessionId, cancellationToken)` and receive the same `SemanticRefreshResult`. A repository and selected solution must already be bound; otherwise both surfaces return a clear error.

## Recovery and safety

- Retry with `/semantic_refresh` after a transient load failure or watcher recovery.
- Reopen the repository when its root or selected solution moved.
- Treat reduced semantic confidence as an honest usable result when the command succeeds; inspect compilation diagnostics separately.
- Refresh configuration cannot disable monitoring, widen watcher roots, lower settling/resource bounds, switch the already-bound solution, select arbitrary or escaping paths, grant read authority, or mark repository writes trusted. Repository `solution.path` may still remember the confined solution selected during normal repository opening.
- Normal lifecycle events contain identities, reason/mode, counts, versions, confidence, duration, and safe failure classification—never source text, raw changed paths, secrets, watcher objects, Roslyn/MSBuild objects, exception dumps, or terminal types.
