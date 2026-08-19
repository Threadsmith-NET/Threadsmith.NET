# Session lifecycle, resume, and clone

Threadsmith stores repository-bound session metadata in the configured SQLite database. The interactive commands and headless APIs use one host transition boundary.

## Commands

- `/new` checkpoints the current session and activates an empty independent session. Repository trust, solution, configuration, tools, and mutation policy remain effective.
- `/resume <session-id>` restores an exact session for the open repository.
- `/resume` opens a newest-first numbered repository session selector. The current session is marked; selecting it is idempotent.
- `/clone` checkpoints the current session and atomically copies its sanitized reconstructible conversation and governed memory into a new top-level session. The confirmation prints `/resume <source-session-id>` on a copyable line.

Transitions require a complete safe boundary. Finish or cancel active model, tool, mutation, validation, skill, hook, or delegated work before retrying.

## Restoration and compatibility

Resume combines tolerant event replay with the conversation archive and persisted session model/reasoning snapshot. Unknown legacy state is reported and is not fabricated. If an exact persisted reasoning level is no longer supported it resets visibly to `none`; a missing or incompatible model reports `/models` guidance rather than silently assigning repository defaults.

Context inspections and process-local provider continuation/cache handles are invalidated. The next request is assembled through the canonical stateless path. Hidden reasoning, raw provider transcripts, credentials, OAuth state, transient activity, and terminal scrollback are neither restored nor cloned.

## Repository scoping and privacy

The selector lists only sessions whose host-derived canonical repository identity matches the currently opened repository. An exact ID from another repository reports the repository display name but never changes working directory, trust, or solution. Picker previews are sanitized and bounded.

A clone receives new message, memory, run, and session identities. It does not duplicate active execution checkpoints, approvals, mutation transactions, worker leases, hook invocations, cancellation sources, or opaque provider continuation handles. Source and clone diverge independently.

## Troubleshooting

- **Session not found:** verify the complete GUID and the configured persistence database.
- **Belongs to another repository:** open that repository explicitly, then resume.
- **Inspectable only:** legacy/future state could not be reconstructed safely; retain the database for diagnostics.
- **Safe-boundary rejection:** cancel or await current work and retry.
- **Model unavailable:** use `/models`, then `/reasoning` if needed.

Migration 8 is additive and transactional. A migration failure leaves the prior schema version readable.
