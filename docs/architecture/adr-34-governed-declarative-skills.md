# ADR-34 — Governed declarative skills and reusable workflows

**Status:** Accepted

## Context

Threadsmith needs reusable procedures without turning repository content into executable plugins, granting prompt text authority, duplicating Plan-37 execution, or allowing packages to schedule Plan-38 children directly. Discovery must scale without placing every package body in context, and private or third-party packages require immutable provenance, integrity, revocation, and safe restoration.

## Decision

Threadsmith owns a scoped metadata-first skill catalog and a closed declarative workflow interpreter.

A skill is a versioned data package containing bounded metadata, declared SHA-256 asset hashes, bounded input/output JSON Schemas, tool/trust/model/host requirements, aggregate budgets, optional bounded agent templates, and an acyclic graph of host-known workflow step kinds. It contains no assemblies, scripts, expressions, arbitrary code, task scheduling, nested skill calls, or direct repository/network/process effects.

Discovery reads `skill.json` only. Explicit verification re-reads the manifest, verifies all confined declared assets and rejects undeclared files, applies revocation, and then requires either a detached ECDSA P-256 SHA-256 signature from a repository-excluding trusted signer or an externally authorized exact digest/publisher/source tuple. Signature trust and invocation enablement are separate. Repository configuration may narrow discovery but cannot add signers, allowlists, enablement, or suppress revocation.

Authorized invocation resolves one explicit scope/id/version/digest and pins it for the complete workflow. Compatibility is evaluated before body loading and at restore/action boundaries. The context loader reads only the current step's required/optional assets, rechecks hashes, decodes strict UTF-8, sanitizes, and enforces content pressure. Schemas use a closed structural subset with no references, regex execution, custom formats, dynamic types, or remote resolution.

Procedure turns use configured models and only declared available tools through the central tool pipeline. Workflow nodes can return only known host action proposals. Planning and mutation continue through Plan 37; delegation continues through Plan 38. Skills cannot approve themselves, create children, merge worktrees, claim validation, or author terminal success.

SQLite migration 5 stores verification provenance, immutable pins, and versioned checkpoints. Restoration re-resolves the exact package and revalidates current trust, revocation, content, schemas, tools, models, phase, and budgets before one legal next step. Events contain identity and bounded lifecycle metadata, never package bodies or raw model payloads.

## Consequences

- Organization, machine, user, repository, and maintained catalogs are searchable without loading procedures.
- Same-id ambiguity requires explicit selection; no directory-order precedence grants authority.
- Workflows are reproducible, inspectable, cancellable, and resumable but intentionally less expressive than code.
- Maintained analyzer, package-upgrade, and PR-review packages exercise the same untrusted-data pipeline as third-party packages.
- Extension assemblies remain the mechanism for new executable capabilities; skills remain declarative orchestration over existing host capabilities.
- Marketplace hosting, Git/PR publication, automatic dependency restore, arbitrary scripting, nested skills, and package-owned concurrency remain outside M12.
