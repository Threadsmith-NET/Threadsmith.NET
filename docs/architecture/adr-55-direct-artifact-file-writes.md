# ADR-55: Direct allowlisted artifact file writes

Status: Accepted

## Context

Saving a previously generated report required change planning, proposal repair, and a second model pass to reproduce it as a CreateFile mutation. Text artifacts in output folders need a simpler operation with a concrete filesystem boundary.

## Decision

Add the compiled, conversation-available `write_file` tool. It writes bounded UTF-8 report/data files directly through the existing tool pipeline, requires TrustedRead plus normal tool availability/policy, declares a file-write side effect, and remains excluded from read-only child toolsets. Source/project changes continue to use governed mutations.

`tools.writeFile.allowedFolders` defaults to `[".inbox"]`. Machine, user, and repository lists can configure literal output folders; higher-precedence lists replace earlier lists, including an explicit empty deny-all list. Relative folders resolve under the active repository and external folders require absolute paths. This is an explicit operator-configured write grant, including when supplied in repository configuration; the central policy's exception is restricted to the compiled writer and does not widen other tools' read or write authority. Repository switches rebind the grant.

The input supplies path and exactly one of content or useLastResponse. The latter resolves the current session's latest prior assistant answer through the host-owned conversation archive, preserving its exact body without a model regeneration. Existing files require explicit overwrite; sibling temporary files are published only after complete writes. Prohibited paths, Git metadata, Threadsmith configuration, AGENTS.md, and symlink/junction traversal remain forbidden. File contents do not enter routine tool output or activity detail.

## Consequences

Report saves no longer allocate plans, mutation proposals, or code-validation work. The folder list is enforceable authority rather than descriptive prompting. Operators should list output folders they intend the model to write; untracked/Git-ignored status alone grants nothing. Existing tool availability lists may need the new tool name added. Conversation retention can make a previous answer unavailable, in which case exact-copy mode fails visibly.
