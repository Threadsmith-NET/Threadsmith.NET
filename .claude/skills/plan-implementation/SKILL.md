---
name: plan-implementation
description: Specifies the procedure for implementing a plan file in the implementation plans "./docs/implementation-plans" folder.
---

# Confluence Pages Fetch

## When to use

- User specifically requests the implementation of a plan.

## Prerequisites

- This skill is intended (for now) to be used with the Pi coding agent with pi-intercom installed and configured. At least one additional instance of Pi should be running in the same repo using a different model.

## Required Inputs
- The user must provide a plan name (e.g., "plan 95") and a corresponding plan file (e.g., "plan-95*.md") in the "./docs/implementation-plans" folder. The plan file should contain a list of steps to be executed.
- Optionally, the user may supply a list of one or more Pi instances available to be sub-agents.

## Workflow

```
- [ ] Step 0: Discover plan file and optional associated Milestone documents.
- [ ] Step 1: Read plan file and milestone documents, as well as `mileestone.md` and `00-shared-context.md` in "./docs/implementation-plans" folder.
- [ ] Step 2: If you have any questions or anything is unclear, as the user for clarification now.
- [ ] Step 3: If current branch is "main", create a new branch appropriately named for the Milestone/Plan.
- [ ] Step 4: Determin sub-agent availability, if any. If the user supplied a list, use those and do not look for any others. If not, use the pi-intercom skill and get a list of available sub-agents. Identify subagents running in the same repo and send a brief test request. Only use the sub-agent if a response if received.
- [ ] Step 5: From the plan, identify and make a note of all test cases if not already in the plan file. These are the tests that will be created AFTER the user has signed off on the implementation. 
- [ ] Step 6: Implement the plan, but do not implement the test cases or update any documentation yet. If sub-agents are available, use the sub-agents for exploration and any other operations that can be parallelized safely. 
- [ ] Step 7: When implementation is complete, ask the sub-agent(s) for a review. Sub-agent(s) should be asked to review that the implementation is fit for purpose compared to the plan, free of bugs, performance issues, unnecessary memory allocations, security issues, guardrail adherence, .NET best practices, and established project architecture. If multiple sub-agents are available, the reviews can be split up. 
- [ ] Step 8: Consider the sub-agent feedback and determine validity. Feel free to ask sub-agent for clarification or if you disagree with the feedback for any reason.
- [ ] Step 9: Implement the agreeed upon feedback, and ask the reviewer(s) to re-review. Go back to step 7 and interate until the reviews are clean from the reviewer(s).
- [ ] Step 10: Generate executive summary of the work that was performed. Ask user to provide sign off.
- [ ] Step 11: When sign off is received, create unit tests for documented test cases from step 5. 
- [ ] Step 12: Add/update any documentation and DOX documents as necessary to complete plan.
- [ ] Step 13: Ask reviewer(s) to review test and documentation updates only
- [ ] Step 14: Assess feedback from reviewer(s) and address any valid concerns. Go back to step 13 and iterate until reviews of documentation/tests ar clean.
- [ ] Step 15: Produce final summary to user. 
```
