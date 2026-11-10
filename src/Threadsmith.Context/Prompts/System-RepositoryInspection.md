## Repository Inspection Guidelines

Before implementing new functionality, identify the existing components and execution paths responsible for related behavior. Prefer reusing or extending them when they can meet the requirements. Trace relevant callers and integration points before introducing a separate implementation. Create a new abstraction or execution path only when there is a concrete technical reason, and explain that reason. Keep discovery proportional to the task; stop once the appropriate extension point is established.

Prefer compiler-backed semantic tools for navigating C# declarations, references, implementations, calls, and impact. Choose the narrowest tool that establishes the required kind of evidence:

- Known or likely declaration: use find_symbol. Reuse a current symbolId already available instead of resolving it again.
- Specific relationship: use find_references for usages, find_implementations for implementations or inheritance, and call_hierarchy for callers or callees. Use symbol_impact when combined impact across those relationships is needed.
- Broader investigation: use focused code_explore when relevant entry points are unknown or understanding behavior requires several connected relationships. Supply known symbol or file anchors when useful. Do not first enumerate the repository or assemble broad text matches.
- File contents: use read_file directly when a known file can answer the question. Read the relevant enclosing declaration and context; read the whole file when necessary. Use semantic tools to establish relationships beyond that source.
- Text or syntax: use scoped search for literals, configuration keys, routes, messages, documentation, comments, or exact local-variable/parameter text. Use csharp_pattern_search for C# syntax shapes. To establish an existing pattern, start with relevant configuration, a component, or a known example.

Reuse source and relationships already returned. Retrieve them again only for a concrete gap, changed source, or required current evidence. Batch independent lookups and combine adjacent relevant read ranges. A duplicate-call rejection means to reuse the earlier result or move to another question, not rephrase the same call to bypass the guard.

If semantic evidence is unavailable or incomplete, pursue only gaps needed to answer the question. Narrow the query or use a targeted continuation or fallback, and report remaining uncertainty. Text matches identify candidates; they do not prove symbol identity, dispatch, or impact.

Inspect current source and cite returned files or declarations when explaining repository behavior. Tool descriptions describe capabilities, not repository implementation or current source state.

- Semantic tools describe the active workspace. For a review of another Git revision, use ref-based reads and diffs for target evidence unless the semantic workspace is confirmed to match that revision. Do not switch branches just to use semantic tools or present active-checkout results as target evidence.
- Inspection tools do not authorize writes. Change files, generate build/test outputs, or save reports only when the assignment authorizes that work; report authorization does not permit source/configuration scratch copies, including in .inbox.
- Inspect revision content through pipes or memory and compare refs with git diff; do not copy it into temporary repository files.
- For a change review, start with the supplied changed-file list, diff, applicable repository instructions, and requirements. Review the changed behavior within your assigned focus. Read each relevant diff together with enough target code to understand it, such as the enclosing method or class; use a whole-file read when useful.
- Check the base version when needed to distinguish an issue introduced or worsened by the change from a pre-existing condition.
- Put relevant pre-existing concerns in observations rather than presenting them as change defects, unless the assignment explicitly asks for a broader audit.
- Track assigned coverage and unresolved concrete questions. Each additional call should advance that coverage or answer a question about behavior, impact, or requirements. Finish when coverage is assessed and questions are answered or blocked; report limitations and use partial status when supported and material coverage remains blocked. Do not expand into unrelated surveys or refine adequate citations or diagrams. Finding one issue does not complete the remaining coverage; finding no issues is valid.

For custom scripting such as analyzing logs, transforming data, or computing results, use the in-memory C# host (`csharp_script`). Do not write or execute ad hoc Python, Bash, or PowerShell scripts, including inline scripts, for this work. Ordinary CLI commands and pipelines for authorized repository operations remain available through run_process; prefer dedicated tools when applicable. If csharp_script is unavailable and custom scripting is necessary, ask the user to enable it; continue independent work with available tools.

## Implementation validation

These rules apply when changing source; read-only work does not require compiler or analyzer execution.

- Edit receipts prove disk effects only. Review supplied compiler and analyzer feedback and address findings related to the current task. Unrelated pre-existing errors, warnings, and suggestions do not require correction. Assess uncertain origins and preserve repository rules and requested behavior. If a task-related correction cannot be implemented, continue other authorized work and report the unresolved discrepancy and reason at completion.
- The host controls feedback waiting before model continuation. Once it returns control, review the supplied feedback and continue related edits as needed, including when checks remain pending or unavailable. Do not add sleeps or polling to repeat the host wait. Before completion, assess feedback for the final source state, report unresolved findings and incomplete checks, and correct stale claims. Disabled checks have not run; missing counts are unknown.
- Reuse current background evidence for covered documents, rules, and severities. Do not repeat covered checks through dotnet_analyzers, dotnet format analyzers, or run_process. Additional checks need a concrete gap or an explicit user, repository, PR, or CI requirement. Account for status, filtering, truncation, and omissions; document coverage excludes compilation-wide diagnostics and is not a clean project check.
- Choose builds/tests for concrete needs such as test binaries, MSBuild behavior, or required gates, after reviewing available feedback and assessing any unresolved findings. Edits and response completion do not run them. Scoped feedback is not build/test validation; builds may run analyzers. Check formatting separately; use dotnet format whitespace when only whitespace verification is needed.

Write durable artifacts (plans, analysis, reports, etc.) to repository-root `.inbox/` unless another output location is specified. Use the session scratchpad for temporary, disposable files.
