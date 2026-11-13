# ADR-55: Direct allowlisted artifact file writes

Status: Accepted

## Context

Saving a previously generated report required change planning, proposal repair, and a second model pass to reproduce it as a CreateFile mutation. Text artifacts in output folders need a simpler operation with a concrete filesystem boundary.

## Decision

Add the compiled, conversation-available `write_file` tool. It writes bounded UTF-8 report/data files directly through the existing tool pipeline, requires TrustedRead plus normal tool availability/policy, declares a file-write side effect, and remains excluded from read-only child toolsets. Source/project changes continue to use governed mutations.

`tools.writeFile.allowedFolders` defaults to `[".inbox"]`. Other destinations require trusted folder grants; external destinations require absolute grants. The active session scratchpad retains its separate implicit grant. Only the designated machine, user, and allowlisted launch-environment inputs establish grants, with whole-array replacement within that trusted stack. Repository and repository-owned session lists intersect those grants by directory containment; an explicitly empty list denies ordinary artifact writes without revoking the active scratchpad's implicit grant. Relative folders resolve under the active repository and external folders require absolute paths. The central policy's exception is restricted to the compiled writer and does not widen other tools' read or write authority. Explicit repository rebinding resolves a fresh immutable policy, including when reopening the same repository. Child writes remain within the assignment and read-only children cannot use a parent's export grant.

This authority amendment removes repository-granted write authority. Existing repository export folders require deliberate trusted configuration; the host never promotes them automatically. Generic file tools cannot modify authority-control files. This is application authorization, not protection against shells or other same-user processes.

The input supplies path and exactly one of content or useLastResponse. The latter resolves the current session's latest prior assistant answer through the host-owned conversation archive, preserving its exact body without a model regeneration. Existing files require explicit overwrite; sibling temporary files are published only after complete writes. The existing `AuthorityControlPathGuard` protects authority-control files, secret stores, and configured control paths. Explicit prohibited paths and symlink/junction traversal remain forbidden. Git metadata and AGENTS.md instructions are also rejected for ordinary artifact writes, even under an allowed folder. File contents do not enter routine tool output or activity detail.

## Consequences

Report saves no longer allocate plans, mutation proposals, or code-validation work. The folder list is enforceable authority rather than descriptive prompting. Operators should explicitly grant only output folders they intend the model to write; untracked/Git-ignored status alone grants nothing. Existing tool availability lists may need the new tool name added. Conversation retention can make a previous answer unavailable, in which case exact-copy mode fails visibly.
