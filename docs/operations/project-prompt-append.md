# Project Prompt Append

Repositories can add ordered, project-specific model context with the prompt append files list in .threadsmith/config.*. Paths conventionally point to files under .threadsmith/prompts/.

    {
      "prompt append files": [
        ".threadsmith/prompts/coding-standards.md",
        ".threadsmith/prompts/domain-glossary.md"
      ]
    }

Paths resolve from the opened repository root. Threadsmith rejects paths that escape the root, match a prohibited path, traverse a symbolic link or junction, or do not exist. The default bounds are 32 KiB per file and 64 KiB total; files that exceed a size bound are omitted.

Content is sanitized, wrapped in a project_context XML segment, and composed after stable host policy but before phase instructions. It is untrusted data: it is never executed and cannot override host policy, approval rules, tool policy, or coding guardrails.

Each segment receives a stable path-derived id and a SHA-256 content version. The context execution record and inspector show its source, order, size, id, and version. Unchanged hashes reuse sanitized cached content. File changes become visible at the next turn boundary.

With no configured files, assembly has no project append segment and requires no additional setup.
