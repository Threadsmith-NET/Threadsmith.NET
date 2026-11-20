## Repository Inspection Guidelines

Trace the existing path and its callers before adding functionality. Reuse or extend it unless there is a concrete technical reason not to; explain that reason. Stop searching once the extension point is clear.

Choose the narrowest tool that answers the question:

- For a known C# declaration, including one identified by search or code_explore, reuse its current symbolId or get one with find_symbol. Follow it with find_references (usages), find_implementations (implementations or inheritance), call_hierarchy (callers or callees), or symbol_impact (combined impact) as needed.
- Unknown entry point or multi-hop behavior: use focused code_explore with any known anchors, not a repository inventory.
- For a known file, use read_file directly. If a finding cites a line, read the enclosing code first. Expand the range as needed.
- Use scoped search for exact text, configuration, or a name that find_symbol could not resolve. For C# syntax shapes, use csharp_pattern_search. Start pattern checks with a relevant component, configuration, or example.

Batch independent lookups and combine adjacent reads. Reuse inspected ranges on follow-ups. Check the current diff for changes and read only the ranges needed to fill a gap or obtain required fresh evidence. Use a targeted fallback for unresolved questions. Never rephrase a rejected duplicate to bypass the guard.

Explain behavior from inspected source, cite files or declarations, and report uncertainty. Tool descriptions are not source evidence; text matches do not prove symbol identity, dispatch, or impact.

- Semantic tools describe the active workspace. Unless it matches the target revision, use ref-based reads and git diff through pipes or memory. Do not switch branches for semantic tools or copy revision contents into temporary files.
- Inspection does not authorize edits, build/test outputs, or reports. Permission to save a report does not permit scratch copies of source or configuration, even in .inbox.
- Review the supplied changed-file list and diff against the requirements and applicable repository instructions. Read each relevant hunk with enough surrounding target code to understand the behavior. Compare with the base when needed; report pre-existing concerns as observations, not change defects, unless a broader audit was requested.
- Cover the assigned scope even after finding an issue. Stop when the remaining questions are answered or blocked. Report material gaps and use partial status when supported; no findings is a valid result.

{{ImplementationValidation}}

Write durable artifacts (plans, analysis, reports, etc.) to repository-root `.inbox/` unless another output location is specified. Use the session scratchpad for temporary, disposable files.
