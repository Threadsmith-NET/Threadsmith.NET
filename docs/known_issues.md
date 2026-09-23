# Known issues

Issues recorded here are deferred for later review. They do not change the current implementation contract.

## PR evidence handoff to narrowly scoped children

**Status:** Deferred edge case.

After a parent calls `pr_fetch`, the child model loop offers the captured PR snapshot to every delegated child. `read_agent_evidence` checks that the snapshot ID was delivered to that child, but does not apply the child's approved roots or prohibited paths to the snapshot's file inventory or diff. A child assigned a narrower file scope could therefore read PR changes outside that scope.

In the maintained PR review workflow, specialists intentionally receive the same complete PR and focus on different aspects of it. The issue arises if a parent fetches a PR and then delegates an unrelated or narrowly scoped task. There is no known impact on the intended full-PR review workflow.

**Revisit when:** PR evidence handoff is used outside full-PR review, or child path scopes are expected to constrain inherited PR evidence. Decide whether to restrict handoff to children authorized for the whole PR or to filter or deny snapshots at the child boundary. Add a mixed-scope regression test for the chosen behavior.

**Relevant code:** `src/Threadsmith.Execution/ChildAgentModelLoop.cs`, `src/Threadsmith.Execution/ChildAgentEvidenceTool.cs`, and `src/Threadsmith.Tools/PullRequests/PrFetchCache.cs`.
