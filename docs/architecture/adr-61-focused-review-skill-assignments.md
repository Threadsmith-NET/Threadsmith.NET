# ADR-61 — Model-driven review skills

Status: Amended; the original private-review workflow is retired.

## Decision

The maintained `review` package is an ordinary native model procedure. Its editable `prompts/Skill-Review.md` directs evidence gathering with advertised tools, five specialist assignments through `delegate_agents`, structured response guidance, model synthesis and delivery through `write_file` or the window.

The root uses the selected session model and reasoning frozen at invocation. Children use ordinary role configuration, falling back to that parent selection. Native model calls capture the same request-lifetime tool registrations as conversations and release them after tool execution/child joining. Host trust, tool enablement, approvals, scheduling and cancellation remain authoritative.

`inherit` passes the parent's enabled, permitted tools to a `SharedWorkspace` child, including process execution, file tools, and skills when available. Tool metadata `SubagentAvailable` defaults to true; tools marked false are removed, including `delegate_agents`. Explicit `readOnly` keeps a narrower non-network inspection surface. Child-invoked skills preserve the filtered caller scope and model provenance through the shared pipeline. Caller snapshots are transient. Explicit host resume and continuation use the existing durable checkpoint and current-session revalidation, preserving host-action workflows after the calling request ends.

No compiled recipe, private reviewer package, capture store, special reader, per-reviewer validator, deterministic deduplication, formatter or report writer remains. Remote acquisition uses existing advertised tools such as `run_process`; ordinary Git/read tools continue to address the invoking workspace. Prompts must supply remote evidence to children when it resides elsewhere.

Remote fetch requires the ordinary tool's trust, executable allowlist and approval configuration; package compatibility does not promise a usable fetch tool. Commit SHAs allow stable committed-source reads. Working-tree stability and coordinating shared edits remain part of the assigned task, without a review-specific freeze mechanism.

## Consequences

Reviewer wording and review decisions are editable prompt content. The model consolidates advisory child responses; the host does not judge their findings. Generic native manifests can map a boolean success property and string response property to ordinary skill completion and presentation. Failed procedures can be explicitly resumed, which reruns the failed step and may repeat model/tool work. Previous private review records are not resumed by the new package; changed package digests fail ordinary revalidation. Reports use the normal file tool's permissions and behavior.

Specialist coverage and existing-inbox delivery are prompt instructions, not bespoke host validators. Model-invoked skill output returns through ordinary outer-model tool continuation; manual and headless invocation display the response directly. No canonical-report delivery override is installed.
