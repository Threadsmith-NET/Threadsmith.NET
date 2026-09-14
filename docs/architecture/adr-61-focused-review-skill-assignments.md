# ADR-61 — Model-driven review skills

Status: Amended; the original private-review workflow is retired.

## Decision

The maintained `review` and `review-pr` packages are ordinary native model procedures. Their editable `prompts/Skill-Review.md` directs evidence gathering with advertised tools, four specialist assignments through `delegate_agents`, structured response guidance, model synthesis and delivery through `write_file` or the window.

The root uses the selected session model and reasoning frozen at invocation. Children use ordinary role configuration, falling back to that parent selection. Native model calls capture the same request-lifetime tool registrations as conversations and release them after tool execution/child joining. Host trust, tool enablement, approvals, scheduling and cancellation remain authoritative.

No compiled recipe, private reviewer package, capture store, special reader, per-reviewer validator, deterministic deduplication, formatter or report writer remains. Remote acquisition uses existing advertised tools such as `run_process`; ordinary Git/read tools continue to address the invoking workspace. Prompts must supply remote evidence to children when it resides elsewhere.

## Consequences

Reviewer wording and review decisions are editable prompt content. The model consolidates advisory child responses; the host does not judge their findings. Generic native manifests can map a boolean success property and string response property to ordinary skill completion and presentation. Failed procedures can be explicitly resumed, which reruns the failed step and may repeat model/tool work. Previous private review records are not resumed by the new package; changed package digests fail ordinary revalidation. Reports use the normal file tool's permissions and behavior.
