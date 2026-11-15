# ADR-55: Direct artifact file writes

Status: Accepted

## Context

Saving a previously generated report required change planning, proposal repair, and a second model pass to reproduce it as a CreateFile mutation. Text artifacts in output folders need a simpler operation with a concrete filesystem boundary.

## Decision

Add the compiled, conversation-available `write_file` tool. It writes bounded UTF-8 report/data files directly through the existing tool pipeline, requires TrustedRead plus normal tool availability/policy, declares a file-write side effect, and remains excluded from read-only child toolsets. Source/project changes continue to use governed mutations. A shared configuration-path classifier, also used by mutation risk assessment, rejects conventional project, tool, build, and runtime configuration destinations before artifact admission and again before publication. This includes `appsettings*.json`, `.config/dotnet-tools.json`, package manifests, and GitHub action definitions. These writes must use `edit_source`; overwrite flags and external folder grants do not bypass this check. The active scratchpad remains temporary storage, not a configuration deployment path.

Supported artifacts within the active repository use existing host-owned `MutationRoots`, narrowed by child assignment scope, independently of `WriteFolders`. No separate repository artifact-root mechanism is introduced. The active session scratchpad retains its separate implicit grant. `tools.writeFile.allowedFolders` retains its compatibility default `[".inbox"]`, but no longer limits repository writes. `.inbox` remains the recommended report location. An explicitly empty list denies external exports only.

External destinations require trusted absolute folder grants. Designated machine and user configuration, user-owned repository permissions, and supported launch inputs can establish grants; checked-in repository and repository-owned session configuration cannot. Trusted arrays replace lower trusted arrays; explicit machine enforcement restricts export grants. Relative entries resolve under the active repository. Explicit repository rebinding resolves a fresh immutable policy, including when reopening the same repository. Child writes remain within the assignment, and read-only children receive no direct-write authority. This compiled capability does not widen other tools' read or write authority. Generic file tools cannot modify authority-control files. This is application authorization, not protection against shells or other same-user processes.

The input supplies path and exactly one of content or useLastResponse. The latter resolves the current session's latest prior assistant answer through the host-owned conversation archive, preserving its exact body without a model regeneration. Existing files require explicit overwrite; sibling temporary files are published only after complete writes. The existing `AuthorityControlPathGuard` protects authority-control files, secret stores, and configured control paths. Explicit prohibited paths and symlink/junction traversal remain forbidden. Git metadata and AGENTS.md instructions are also rejected for ordinary artifact writes, even under an allowed folder. File contents do not enter routine tool output or activity detail.

## Consequences

Report saves no longer allocate plans, mutation proposals, or code-validation work. Repository artifact saves reuse host-owned mutation scope rather than requiring output-folder grants. External export grants remain enforceable authority rather than descriptive prompting. Operators should grant only external folders they intend the model to write. Git-tracked, untracked, or ignored status does not bypass safety checks. Existing tool availability lists may need the new tool name added. Conversation retention can make a previous answer unavailable, in which case exact-copy mode fails visibly.
