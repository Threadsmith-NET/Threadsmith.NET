# Code reviews with the maintained review skill

[Back to the user guide](user-guide.md#governed-skills-and-reusable-workflows)

`Maintained:review@1.0.0` reviews a branch or a focused set of changes with five specialist agents. A lead model gathers the review context, delegates the specialist work, then consolidates their responses into a readable Markdown report. The lead uses the model and reasoning selected in the TUI. Specialists use their configured role assignments, with the calling model and reasoning as fallback.

The examples below use this Threadsmith.NET checkout at `C:\source\repos\Threadsmith` and its repository, `https://github.com/Threadsmith-NET/Threadsmith.NET.git`. Run the commands inside Threadsmith with that repository open. The remote example uses the feature branch on which this skill was developed; substitute another source branch when reviewing later work.

## Before starting

Choose the lead model with `/models` and its supported reasoning level with `/reasoning`. Open `/skills`, find **Native → Maintained → review**, and verify and enable it. In the dialog, use **F3 → Verify** for verification and **Space** for enablement. The equivalent commands are:

```text
/skills inspect Maintained:review@1.0.0
/skills verify Maintained:review@1.0.0
/skills enable Maintained:review@1.0.0
```

Use `/tools` to enable `delegate_agents` and the tools needed for your review. The lead can gather evidence through Git tools, file/search/semantic tools, or `run_process`; it chooses from the enabled tools. Enable `invoke_skill` if you want to start the review through natural language. Enable `write_file` if you want a saved report. Fetching branches or running tests through `run_process` needs the usual process configuration and permissions. Skill verification does not grant tool permissions; the skill requires at least `TrustedRead`, and individual tools keep their normal requirements.

The default `agents:delegation:maximumAgents` is five, which lets the lead request all specialists in one delegation call. This is separate from the number that can run simultaneously. For five simultaneous reviewers, merge these settings into `%USERPROFILE%\.threadsmith\config.json` and restart:

```json
{
  "agents": {
    "maxActiveGlobal": 5,
    "maxActivePerParent": 5
  }
}
```

Preserve your other settings. A smaller active concurrency can queue admitted assignments; a smaller positive `maximumAgents` rejects an oversized delegation call, so the lead would need to split the work into batches. See [parallel-agent configuration](operations/parallel-agents.md#configuration), [tool availability](user-guide.md#tools-and-tool-availability), and [skill operations](operations/skills.md) for details.

## Invoke with JSON

Enter each `/skills use` command as one line. The JSON is the skill's input, and URLs inside it should be plain strings, without Markdown link formatting.

### Review the current branch

Compare committed changes on the current branch with `main`:

```text
/skills use Maintained:review@1.0.0 {"mode":"currentBranchChanges","baseBranch":"main","instructions":"Review the committed changes on the current branch against main. Exclude uncommitted changes."}
```

To include working changes, say so in `instructions`. Specify the intended base explicitly when it matters.

### Review a pull request

Configure a GitHub.com or Bitbucket Cloud account and enable `pr_fetch` as described in [PR retrieval](operations/pr-fetch.md). Supply the PR link in conversation, or invoke:

```text
/skills use Maintained:review@1.0.0 {"mode":"pullRequest","url":"https://bitbucket.org/workspace/repository/pull-requests/123"}
```

The lead calls `pr_fetch` with `kind:"diff"` once to obtain metadata, the complete changed-file inventory, and all available diff content before delegation. The host handles provider pagination. Specialists receive the acquired evidence; `pr_fetch` is absent from their catalogs. Retrieval failures and provider omissions are reported explicitly.

### Review a remote branch

```text
/skills use Maintained:review@1.0.0 {"mode":"remoteBranch","repository":"https://github.com/Threadsmith-NET/Threadsmith.NET.git","branch":"feature/plan-108-focused-review-skills","baseBranch":"main"}
```

The shipped prompt tells the lead to use a PR diff when available. Otherwise it reuses suitable local refs or fetches the two requested refs and enough targeted history to resolve their merge base. The review covers changes introduced on the target branch since that merge base. If the merge base cannot be resolved, the review stops instead of substituting a direct comparison of the two branch-tip trees, which could misclassify newer base-branch work as a target-branch change. Acquisition uses the ordinary enabled tools and their configured repository access.

### Review specific behavior against documentation

This example focuses on raw model-log rotation at startup and `/new`, using the repository's model-provider documentation as requirements context:

```text
/skills use Maintained:review@1.0.0 {"mode":"specialInstructions","paths":["src/Threadsmith.Models/ModelExchangeLogging.cs","src/Threadsmith.Execution/SessionLifecycleApplication.cs","src/Threadsmith.App/ModelComposition.cs","tests/Threadsmith.ModelTooling.Tests/JsonlModelExchangeLogTests.cs"],"instructions":"Review raw-model-log rotation at startup and /new. Check that archives use the next numeric suffix for the same root name, existing contents are preserved, and logging continues at the original path.","requirementsDocumentPath":"docs/operations/model-providers.md","requirementsSource":"workspace"}
```

`requirementsSource: "workspace"` means the requirements file is in the invoking checkout. Use `"reviewTarget"` to read it from the branch or tree under review. `paths` narrows the requested scope; the reviewers can inspect supporting callers and dependencies as needed.

The available `review` input fields are:

| Field | Purpose |
| --- | --- |
| `mode` | `currentBranchChanges`, `remoteBranch`, `pullRequest`, or `specialInstructions`. |
| `url`, `provider` | Required PR web URL and optional configured account ID for `pullRequest`; the tool selects the matching account from configured URL patterns. |
| `baseBranch` | The comparison base. |
| `repository`, `branch` | The remote repository and source branch. |
| `instructions` | Review goals, situational instructions, and whether to include working changes. |
| `paths` | Repository-relative paths to focus on. |
| `requirementsDocumentPath`, `requirementsSource` | A requirements file and whether it comes from `workspace` or `reviewTarget`. |

Use `url` for a PR link; there is no `prUrl` field. Normally omit `provider`; the tool reports ambiguity if multiple configured accounts match, so the model can ask which account to use.

## Invoke through natural language

You do not need to write JSON in conversation. With `invoke_skill` available and the package enabled, ask the model to use the exact skill and describe the target. The model constructs the input and calls the normal skill tool.

Current branch:

```text
Use Maintained:review@1.0.0 to review the committed changes on the current Threadsmith branch against main. Exclude uncommitted changes. Pay particular attention to reuse of the existing tool and delegation paths.
```

Remote branch:

```text
Use Maintained:review@1.0.0 to review feature/plan-108-focused-review-skills against main in https://github.com/Threadsmith-NET/Threadsmith.NET.git. Run all five specialists and include any coverage gaps in the report.
```

Focused requirements review:

```text
Use Maintained:review@1.0.0 to review Threadsmith's raw model-log rotation at startup and /new. Use docs/operations/model-providers.md from this checkout as requirements context. Check that only archives with the same root name determine the next suffix, existing contents are preserved, and logging continues at the original path. Include the related lifecycle code and tests.
```

You can also provide a Jira ticket or pasted acceptance criteria. For example:

```text
Before invoking Maintained:review@1.0.0 for the current Threadsmith branch against main, retrieve the Jira ticket I linked using the available Jira tool. Pass its description and acceptance criteria into the review context, especially for BugReviewer. If the ticket cannot be retrieved, ask me for its contents before starting the review.
```

Retrieving a ticket requires an available tool and access to that Jira instance. Alternatively, paste the ticket contents into the request or save them in a local requirements file. The lead passes situational objectives, requirements, and source locations in each delegated task; the specialist's role prompt supplies its general focus. For PR reviews, the host also supplies each specialist a reference to the lead's already captured PR snapshot. Specialists inspect that evidence with `read_agent_evidence`, including selected line ranges for a large diff. They cannot invoke `pr_fetch` to acquire the PR again.

## The five specialists

| Specialist | What it examines |
| --- | --- |
| **SecurityReviewer** | Practical risks appropriate to the application's actual exposure: authorization, injection, secret handling, data exposure, and file/network access. For Threadsmith, this includes tool and workspace boundaries. Possible committed credentials should be flagged by location without reproducing their values. |
| **TestReviewer** | Related tests and relevant test runs, failures, missing coverage, stale or ineffective tests, and unnecessarily complex tests. It distinguishes inspected coverage from tests actually executed. |
| **PerformanceReviewer** | Repeated work, allocations, I/O, algorithmic cost, async behavior, concurrency waits, and resource lifetimes. It distinguishes measured results from concerns inferred from source. |
| **BugReviewer** | Functional bugs and regressions against intended behavior and supplied acceptance criteria. It traces callers, state transitions, error handling, boundary cases, and integration behavior, and checks repository coding standards. Without explicit requirements it looks for demonstrable bugs and identifies assumptions. |
| **ArchitectureReviewer** | Applicable `AGENTS.md`, architecture documents, dependency direction, ownership, public contracts, subsystem boundaries, reuse, duplicated code, and .NET/package usage. For this repo, that includes established execution paths and the separation of core, execution, interaction, and TUI concerns. |

The prompt asks for all five roles. Configured delegation capacity determines how many can run at once; some may wait for a slot. Each receives the normal child-agent context plus its role prompt and the lead's task/context. Specialists can use inherited tools, including processes when enabled. Child catalogs exclude both `delegate_agents` and `invoke_skill`.

Reviewers are asked for JSON containing `status` (`complete`, `partial`, or `failed`), `summary`, `findings`, `coverage`, and `limitations`. Each finding should include severity, title, path, source lines, explanation, evidence, and recommendation. This is prompt-guided reviewer output, not a separate host-enforced per-reviewer schema. An empty findings list is valid; reviewers should not invent issues to fill a quota.

## During the review

Expect a skill activity entry, ordinary tool progress associated with the skill invocation, and specialist tabs when delegation starts. The lead gathers relevant changes and supporting code; reviewers can then inspect additional relevant files themselves. The prompt includes configuration files such as YAML in the review and says that redaction of a credential must not cause an otherwise readable file to be discarded.

The lead reads the joined specialist results and writes the report using the model. It merges findings with the same cause and consequence, retains distinct issues, checks conflicting claims against evidence, and removes unsupported claims. Report synthesis and finding consolidation are directed by the editable prompt.

## What to expect in the report

The shipped main prompt requests these seven sections:

| Section | Expected content |
| --- | --- |
| **Summary** | Scope and purpose of the changes, informed by supplied requirements. Architecture diagrams and BugReviewer call-trace diagrams, when returned, are included here under appropriate subheadings. These may be Mermaid diagrams or ASCII call traces. |
| **Changes** | Added, removed, and edited files, grouped by .NET project. |
| **Issues** | Supported, actionable findings ordered by **P1 (Critical)**, **P2 (Significant)**, and **P3 (Minor)**, with source locations, explanations, evidence, and recommendations. |
| **Observations** | Useful context or potential improvements that may not require a fix. |
| **Requirements coverage** | Gaps between the implementation and supplied requirements. Without a specific requirements document, the prompt requests: “No specific requirements documentation provided.” |
| **Validation and coverage limits** | Test results, specialist completion or failure, unassessed areas, and constraints affecting confidence in the review. |
| **Final recommendation** | **Ship**, **Ship with Observations**, or **Changes Needed**, with a brief explanation when changes are needed. |

A failed specialist should be identified along with the missing coverage; an incomplete review must not be presented as clean. If all specialists fail, the prompt directs the lead to report review failure. Finding real defects is a successful review execution, so a completed/green skill entry can still produce a **Changes Needed** recommendation.

### Where the report goes

If the invoking repository already contains an `.inbox` directory and `write_file` is available, the prompt directs the lead to save the full Markdown as a unique `.inbox/review-<timestamp>.md` file. Create `.inbox` before starting if you want file output, and ensure the ordinary write-tool configuration allows it. The response should include the verdict, coverage/failure summary, and a link to the saved report.

Otherwise, the full report is returned to the window. A failed save should also fall back to the window and explain the failure. Manual invocation displays the skill response; natural-language invocation returns it to the calling model as a tool result for its answer.

## Prompts you can customize

The behavior above describes the current shipped prompts. The lead's instructions control acquisition, delegation, common response guidance, synthesis, and delivery. The specialist prompts control their review focus.

| Purpose | Source file |
| --- | --- |
| Lead | `src/Threadsmith.Skills/Prompts/Skill-Review.md` |
| Security | `src/Threadsmith.Execution/Prompts/System-ChildAgent-SecurityReviewer.md` |
| Tests | `src/Threadsmith.Execution/Prompts/System-ChildAgent-TestReviewer.md` |
| Performance | `src/Threadsmith.Execution/Prompts/System-ChildAgent-PerformanceReviewer.md` |
| Bugs | `src/Threadsmith.Execution/Prompts/System-ChildAgent-BugReviewer.md` |
| Architecture | `src/Threadsmith.Execution/Prompts/System-ChildAgent-ArchitectureReviewer.md` |

In a deployed application, these files live together under `prompts/` beside the app. Restart Threadsmith after editing deployed prompts because they are loaded at startup. See [customizing deployed prompts](user-guide.md#customizing-deployed-prompts) and the [prompt file reference](prompt-file-reference.md). Editing prose changes the requested review behavior; model, tool, trust, and delegation configuration still apply.

## Notice

AI code reviews can be a use code-quality tool, but should be considered advisory and not authoritative. They are best used in conjunction with human reviews.
