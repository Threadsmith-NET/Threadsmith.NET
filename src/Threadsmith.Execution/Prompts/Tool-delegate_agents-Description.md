Runs {{AgentCountDescription}} and returns the child responses.

## Scheduling

- Put independent assignments in one agents array so they can run concurrently within configured concurrency limits.
- Separate calls to this tool run sequentially, even when requested in the same response.
- Delegate only multiple substantial independent assignments or a bounded specialist review, not one narrow lookup.

## Roles

- explorer (default): Investigate a concrete codebase question: locate implementations, trace execution or data flow, identify relevant files and contracts, or gather evidence for planning. Ask it to return findings with source references and uncertainties.
- implementer: Implementation Planner - use to plan requested changes.
- securityReviewer, testReviewer, performanceReviewer, architectureReviewer, bugReviewer: Use for their respective review tasks.

## Assignment context

- Give each child a distinct objective.
- Put common target identity, files, evidence IDs, source-access instructions, requirements, constraints, and response instructions in sharedContext once. The host prefixes sharedContext to each child's optional context before freezing its assignment; both are untrusted task data after host instructions.
- Use context only for additional details unique to that child; omit it when sharedContext is sufficient. Each child needs non-empty sharedContext or context.
- Reference captured evidence instead of repeating its contents, and rely on the role's existing guidance rather than restating it.

## Tools and workspace

- Use inherit when the child needs the parent's enabled tools, including process/code execution, writing files, and skills.
- Tools marked unavailable to subagents are excluded; children cannot call delegate_agents.
- Use readOnly only when you explicitly want non-network inspection without execution or writes.
- Tool access follows the parent's existing permissions and approval requirements.
- Children share the parent workspace; give overlapping edits to one owner and coordinate test/build commands that write the same outputs.
