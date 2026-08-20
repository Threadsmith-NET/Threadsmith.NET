---
name: release-readiness-review
description: Review a .NET repository for public release readiness using bounded documentation, configuration, licensing, and test evidence.
allowed-tools: Read Glob Grep
---

# Release readiness review

Review the repository for public release readiness. Focus on observable repository evidence, not assumptions.

## Procedure

1. Inspect top-level release-facing files:
   - `README.md`
   - `LICENSE`
   - `CONTRIBUTING.md`
   - `CODE_OF_CONDUCT.md`
   - `.gitignore`
2. Inspect release and planning docs when present:
   - `docs/user-guide.md`
   - `docs/operations/release-packaging.md`
   - `docs/third-party-license-inventory-status.md`
   - `docs/implementation-plans/milestones.md`
3. Search for obvious release blockers:
   - `TODO`
   - `FIXME`
   - `password`
   - `secret`
   - `api key`
   - `private`
   - `internal only`
4. Check whether sample config files are trackable and real local config/secrets are ignored.
5. Summarize findings as:
   - Ready
   - Needs attention before public release
   - Optional follow-up

## Output

Return a concise Markdown report with:

- release readiness verdict
- evidence inspected
- blockers
- non-blocking risks
- suggested next checks
