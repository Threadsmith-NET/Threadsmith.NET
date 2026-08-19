# Claude-Style Skill Compatibility Specification v1

## Pinned sources

Retrieved 2026-08-10:

- Agent Skills specification: <https://agentskills.io/specification>
- Anthropic Claude Code skills documentation: <https://docs.anthropic.com/en/docs/claude-code/skills> (Claude Code documentation current on the retrieval date). Claude-only fields remain a separate extension layer and are never described as portable Agent Skills behavior.

Checked-in tests consume sanitized local fixtures and do not read a local Claude installation or call the network.

## Portable layer

A candidate is one immediate directory under an approved `.claude/skills` root containing strict-UTF-8 `SKILL.md`:

- `name` is required, 1–64 lowercase ASCII letters/digits/hyphens, has no leading/trailing/consecutive hyphen, and exactly matches the parent directory.
- `description` is required and 1–1024 characters.
- `license` and `compatibility` are inert bounded text.
- `metadata` is inert data.
- experimental `allowed-tools` is a space-separated advisory list; it never grants permission.
- Markdown after frontmatter is untrusted procedural text.

Recommended `scripts/`, `references/`, and `assets/` directories are resources. Executables never run automatically.

## Claude extension layer

The adapter recognizes bounded scalar metadata including `disable-model-invocation`, `user-invocable`, `argument-hint`, `model`, `context`, `agent`, and `hooks`. Model hints cannot select an unconfigured model. Forked contexts, agents, hooks, dynamic command injection, and executable assumptions are restricted or unsupported unless a separate host-owned governed capability represents the same action.

## Parser and filesystem limits

The v1 implementation accepts a deliberately safe scalar YAML-frontmatter subset. It rejects duplicate keys, indentation/nested values outside the pinned subset, aliases, anchors, tags, merge keys, malformed delimiters, invalid UTF-8, controls, and configured byte/count overages.

Default discovery is immediate and metadata-only under explicit repository and user roots. The configured root itself must not be a link or reparse point. Activation resolves the candidate from the current catalog generation, reparses and compares frontmatter, revalidates containment, and walks directories without following linked/reparse descendants. It caps files and aggregate bytes and computes SHA-256 over normalized relative path, length, and raw bytes in deterministic order. Text resources use strict UTF-8. Binary/executable resources are included in identity but not injected into model context.

## Tool mapping v1

| Source name | Host tool id |
|---|---|
| `Read` | `read_file` |
| `Glob` | `list_files` |
| `Grep` | `search_text` |
| `WebSearch` | `web_search` |

The current registry, repository availability, trust, phase, consent, and tool policy remain authoritative. Unmapped tools produce `CompatibleWithRestrictions`; hook/agent/fork requirements produce `Unsupported`.
