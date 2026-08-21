# Threadsmith.NET

A .NET-native, terminal-first coding harness where the host owns control flow and the model is a pluggable reasoning engine—not an autonomous actor.

Threadsmith.NET opens real .NET repositories with Roslyn and MSBuild, gives models governed read-only tools, requires host-validated plans before repository changes, stages mutations transactionally, and runs confidence-aware build and test validation. The interactive interface preserves native terminal scrollback, selection, copying, and responsive bulk paste; equivalent headless operation supports scripts and CI.

## Current state

Threadsmith.NET is currently under active testing and is best described as pre-alpha. I am testing its tools and other functionality, but not everything has been thoroughly validated. Until version 1.0, the project is presented as-is. There are still going to be a lot of bugs, but I am working through them.

If you are interested in helping, contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for details or contact me at [mwright556@gmail.com](mailto:mwright556@gmail.com). Areas that need substantial testing include the Codex provider and its authentication flow, as well as MCP support—particularly SSO authentication flows. This work will take time, and I maintain the project alongside a demanding full-time job.

I aim to provide complete cross-platform installers, and the groundwork is already in place. I do not own or have access to a Mac, so macOS testing and packaging are major areas where help would be especially valuable. GitHub-hosted releases and the remaining release automation are also planned, but are not yet complete. I will not however be using any paid Github features, as I can't pay out-of-pocket to give software away.

Detailed implementation and milestone status remains in [the milestone plan](docs/implementation-plans/milestones.md).

## Key principles

### Why Threadsmith?

Many coding agents treat a repository primarily as text and rely on a repeated edit/build/test/feedback loop to discover whether a change was valid. Threadsmith combines model reasoning with host-owned discovery, governed mutation, and targeted validation. It can use compiler-backed semantic knowledge when the repository is trusted for build execution, but it retains a text-only path when semantic evaluation is unavailable, unnecessary, or inappropriate. Semantic awareness does not make the model intrinsically smarter; it gives the model better evidence and gives the host stronger ways to constrain and verify its work.

This approach should reduce common large-project failures through:

- symbol-aware navigation rather than ambiguous text matching;
- accurate caller, reference, and dependency discovery;
- safer rename and syntax transformations;
- compiler-aware project and solution context;
- early detection of syntax and binding problems;
- affected-project build and test selection;
- exact baseline and external-change detection;
- bounded correction using normalized diagnostics;
- prevention of out-of-plan or stale mutations.

The expected benefit is greatest for unfamiliar or large C# solutions, multi-project changes, refactoring, API evolution, and work where dependencies or overloads make text-only edits unreliable. When enabled, earlier semantic feedback can reject many malformed or incorrectly bound changes before a full build, while affected-project validation avoids repeatedly building and testing unrelated work. For simpler changes, ordinary text mutations still pass through host-owned scope, baseline, exact-diff, approval, and validation gates. Bounded context, conversation compaction, and structured evidence should also reduce the amount of unchanged source, transcript, and diagnostic text sent back to the model.

### Who is Threadsmith for? ###

Short answer - me. There is no shortage of coding harnesses out there (in fact, I almost named this YACH - Yet Another Coding Harness). I'm a huge .NET fan and have been using it consistently since version 1.0 in the early 2000s. IMO it's the best general-purpose language available. Not perfect by any means, but over the last 8+ years it has truly become a viable cross platform, high-performance option suitable for large scale, enterprise development projects. You may disagree. We all have our favorites. Anyone who knows me also knows my preference for strongly-typed languages. In fact, I believe that - especially for agentic engineering - that type safety and more structure allow for agents to produce _better_ code that is cleaner and less prone to runtime errors.

Maybe I am completely wrong - but "It's my repo and I'll preach if I want to." :D

I hope someone finds this tool, and more importantly, _this approach_ useful. It could certainly be applied to other languages. In fact, I recently read an article outlining some similar thoughts in _Towards Data Science_. 

### Choose trust deliberately

Repository trust is cumulative and controls what the host may inspect or execute. It is separate from model capability, tool enablement, mutation approval policy, and per-invocation consent.

| Trust level | What it permits | When to use it |
|---|---|---|
| `UntrustedInspection` | Safe repository configuration inspection and candidate discovery without reading repository file content or executing repository-controlled code. | Start here for an unknown, downloaded, or potentially hostile repository. Review what Threadsmith discovers before granting more access. |
| `TrustedRead` | Repository file reads, text search/indexing, solution/project selection, target-framework inventory, text baselines, text-only discovery, and mutation preview/staging. It does not execute MSBuild or repository build logic. | Use when you trust the source content enough to inspect it but do not trust the repository to execute code. This is also the appropriate mode when you intentionally want to bypass compiler-backed semantic evaluation. |
| `TrustedBuild` | Everything in `TrustedRead`, plus restore, MSBuild evaluation, analyzers, source generators, compilation, tests, compiler-backed semantic discovery, and approved build/process tools. Repository-controlled code may execute. | Use for repositories whose build scripts, dependencies, analyzers, generators, and tests you trust. Choose this when semantic navigation and authoritative build/test feedback are worth the execution risk. |
| `TrustedMutation` | Everything in `TrustedBuild`, plus commit of explicitly authorized mutations inside configured repository roots. | Use when Threadsmith should implement changes, not merely inspect, analyze, or stage them. Exact diffs, mutation policy, path restrictions, baseline checks, and validation still apply. |
| `FullyTrustedAutomation` | The highest-trust explicitly configured automation capabilities, including eligible tools such as isolated C# scripting. | Reserve for repositories and workflows you strongly trust and where the additional automation is required. It does not disable hard host guardrails or automatically grant every tool or mutation approval. |

Use `--trust <level>` at startup, or `/trust inspect`, `/trust read`, `/trust build`, and `/trust mutation` in the interactive terminal. Higher trust can be persisted for the normalized repository path, so grant only the minimum level needed for the task.

Threadsmith may carry more orchestration overhead than a conventional agent for a small, isolated text or configuration edit. Roslyn analysis also does not replace MSBuild, source generators, analyzers, target-specific compilation, non-C# build steps, or meaningful tests. Final quality still depends on model capability, repository guidance, evidence selection, architectural constraints, and test quality.

Accordingly, Threadsmith aims for better **validated completion per token and per minute**, fewer speculative edits and correction loops, and lower cost variance—not simply the fewest model turns. Those advantages should be measured with representative tasks and identical models rather than assumed from architecture alone.

- **Human-governed changes** — models cannot authorize their own mutations.
- **Host-owned control flow** — trust, policy, approvals, path checks, application, and validation remain outside the model.
- **End-to-end cancellation** — cancellation flows through model, tool, process, workspace, build, and test boundaries.
- **Repository containment** — approved roots, prohibited paths, reparse-point checks, and transactional writes protect the workspace.
- **Structured file lifecycle** — create, delete, move, case-only move, and move-plus-edit remain accepted-plan-scoped, identity-bound, exactly previewed, compensating, rollback-protected mutations rather than shell commands.
- **Replaceable integrations** — model providers, extensions, MCP, Roslyn, and terminal libraries stay behind host-owned contracts.
- **Provider-neutral secret discovery** — typed static credentials stay outside ordinary configuration and resolve only at final privileged boundaries through fixed environment → eligible repository → user precedence; repository stores must be confined, untracked, and effectively ignored, while higher-trust consumers such as Brave, MCP, hooks, and private NuGet sources reject repository values.
- **Interactive/headless parity** — the terminal is a projection of the same application commands used by automation.
- **Bounded conversation continuity** — sanitized visible turns and provenance-linked governed memory preserve useful context without unbounded transcript replay.

## Requirements

- .NET 10 SDK
- PowerShell for the examples below, but Bash, GitBash, etc. should all be supported, however every combination has not yet been tested. 

## Quick start

Build the repository:

```powershell
dotnet restore src\Threadsmith.sln
dotnet build src\Threadsmith.sln
```

Launch the interactive terminal from the repository you want to inspect:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --tui
```

When running Threadsmith against its own source tree:

```powershell
dotnet run --project src\Threadsmith.App -- --tui
```

Run a headless request:

```powershell
dotnet run --project src\Threadsmith.App -- "inspect this repository"
```

Open a specific repository and solution:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo --trust TrustedRead --solution src\MyRepo.sln
```

The current directory is the default repository. Interactive startup guides trust and ambiguous solution selection, remembers successful solution choices, and can initialize minimal `.threadsmith/config.json` configuration for an empty repository. Adjust any of the other flags to personal preference of immediate need.

### SQLite
Threadsmith keeps its SQLite session store and content-addressed artifacts under `.threadsmith/` by default. Startup applies transactional migrations, audits persisted content for secrets, and runs configured age-based retention. Static credentials belong in the separate owner-protected `~/.threadsmith/secrets/config.json` user store, an eligible confined/untracked/ignored repository store, or exact `THREADSMITH_` environment variables—not ordinary configuration or command arguments. Consumer trust determines which sources are eligible; lifecycle-owned Codex and MCP OAuth token caches remain separate. See the [user guide](docs/user-guide.md#secrets) and [secret-discovery operations](docs/operations/secret-discovery.md).

## Tools and governed changes

Threadsmith registers twenty-eight built-in runtime tools. Repository configuration, trust, invocation policy, semantic-workspace availability, and user approval determine which tools are available for a request. 

**Notable Features**
- Tools may be enabled/disabled at the machine, user, and repository levels for any reason (such as to reduce context size by removing unused tools)
- Multithreaded parallel tool execution when appropriate. System evaluates sequential/parallel execution opportunities based on tool metadata, dependencies and model request. 

| Tool | Purpose |
|---|---|
| `list_files` | List files beneath an approved repository root. |
| `read_file` | Read a bounded range from an approved repository file. |
| `search` | Search text in bounded, approved repository files. |
| `git_status` | Report bounded Git branch and working-tree status. |
| `git_diff` | Compare working-tree, staged, commit, range, or merge-base changes. |
| `git_log` | Read bounded local commit history. |
| `git_show` | Inspect a bounded local commit, tree, tag, or blob. |
| `git_blame` | Attribute a bounded line range without invoking external helpers. |
| `git_compare_branches` | Report merge base, ahead/behind counts, and changed paths. |
| `dotnet_inventory` | Report normalized solution, project, TFM, reference, package, and test inventory. |
| `nuget_health` | Inspect existing dependency assets and optional trusted-source package advisories. |
| `dotnet_build` | Run a typed exploratory no-restore build. |
| `dotnet_analyzers` | Run analyzers through a typed no-restore build. |
| `dotnet_format_check` | Verify formatting without applying changes. |
| `diagnostic_query` | Query normalized diagnostics from exploratory native runs. |
| `test_discover` | Discover bounded stable test identities in a selected test project. |
| `test_run_targeted` | Run one host-issued test identity with a generated exact filter. |
| `find_symbol` | Find compiler symbols with stable identity and semantic confidence. |
| `find_references` | Find references to a compiler symbol. |
| `find_implementations` | Find implementations of a compiler symbol. |
| `call_hierarchy` | Traverse bounded incoming/outgoing compiler-known calls with dispatch, cycles, confidence, and omissions. |
| `symbol_impact` | Explain bounded reference, caller, implementation, dependent-project/test, and classified-source impact. |
| `csharp_pattern_search` | Search declarations and expression shapes with a closed, inert, versioned C# pattern schema. |
| `generated_code_query` | Inventory and optionally inspect bounded generated content already loaded by the semantic workspace. |
| `run_process` | Run an allow-listed, approved, non-interactive process with bounded output and process-tree cancellation. |
| `datetime` | Return current UTC and local date/time with timezone information. |
| `csharp_script` | Run bounded C# in a fresh isolated worker; disabled by default and reserved for fully trusted automation. |
| `web_search` | Search the web through Brave after explicit repository-scoped outbound consent; disabled by default, with bounded results treated as untrusted evidence. |

Loaded extensions and configured MCP servers may contribute additional tools to the same governed pipeline. At `TrustedRead` or higher, the conditional `invoke_skill` tool lets a model invoke an explicit enabled, compatible declarative skill during evidence collection; it cannot invoke another skill recursively or grant itself additional tools, trust, models, agents, approval, or mutation authority.

`propose_plan` is also a model-callable tool, but it is intentionally not a general-purpose runtime tool. It is a phase-gated workflow signal: during the initial conversational/evidence turn, the model uses its schema-constrained arguments to ask the host to begin governed planning. It does not read, execute, or mutate repository content, cannot be used outside that phase, and never authorizes its own proposal.

The built-in repository inspection and semantic tools do not directly edit, create, or delete repository code. An approved `run_process` command is the exception: the child process runs in the repository working directory and may have direct filesystem side effects that do not pass through Threadsmith's staged mutation, diff-approval, or rollback lifecycle. For host-managed changes, after the user accepts a proposed plan, the same run enters bounded implementation with eligible read-only tools and proposal-only `propose_mutations`. The host validates plan-step correlation and scope, stages supported text or semantic changes in a private workspace, shows and records the exact diff, and transactionally applies only changes permitted by trust, path, baseline-hash, and approval policy. `csharp_script` executes code but is not a file-editing mechanism; `propose_plan` requests this governed workflow but does not perform it.

See the [user guide](docs/user-guide.md#tools-and-tool-availability) for tool availability and policy configuration, and [how repository changes are governed](docs/user-guide.md#how-repository-changes-are-governed) for the complete mutation lifecycle.

## Governed skills quick example

Milestone 12 adds metadata-first, immutable declarative skills and resumable workflows. Maintained packages ship with the host and use the same trust, model, tool, planning, delegation, mutation, and validation boundaries as third-party packages.

Milestone 13 adds host-owned typed lifecycle hooks with advisory-by-default executable, HTTP, MCP, and extension adapters. Repository declarations require exact external approval and remain advisory/fail-open; only repository-excluding managed policy can grant bounded blocking authority at eligible pre-action points. Migration 6 stores approvals and redacted audit outside repository control.

```text
/skills list analyzer
/skills inspect Maintained:fix-analyzer-warnings@1.0.0
/skills verify Maintained:fix-analyzer-warnings@1.0.0
/skills use Maintained:fix-analyzer-warnings@1.0.0 {"diagnostics":["CA1822: Member 'Normalize' does not access instance data"],"scope":["src/Example/Normalizer.cs"]}
```

The invocation may return a typed `ProposePlan` action. That payload is only a proposal: normal plan review and approval, exact-diff policy, transactional application, affected build/test validation, and correction still apply. A custom skill may similarly propose bounded Plan-38 exploration, implementation, or specialist-review agents; the host validates and schedules those children, and `/agents <delegation-id>` reports their durable state. Skill model requirements narrow the configured provider catalog by workload, capabilities, context, sensitivity, and optional profile IDs—the package never downloads or chooses an unconfigured model.

See [governed skills and reusable workflows](docs/user-guide.md#governed-skills-and-reusable-workflows) for complete analyzer-fix, package-upgrade, PR-review, model-selection, subagent, lifecycle, continuation, cancellation, and recovery examples.

## How to contribute

Bug reports, feature proposals, code changes, and documentation improvements are welcome. Participation is governed by our [Code of Conduct](CODE_OF_CONDUCT.md). Before opening a pull request, review the repository's development setup, coding guardrails, testing expectations, DOX workflow, and submission checklist in [CONTRIBUTING.md](CONTRIBUTING.md).

Currently, I am using the fork-and-pr method of accepting contributions.

Please keep changes focused, include meaningful tests for behavior changes, and avoid placing credentials or private repository content in issues, fixtures, logs, or screenshots.

### Statement on AI-generated Code

I used AI substantially (but not exclusively) for this project. Everything was done over the course of weeks, using a very focused, incremental process that was quite time-consuming and also intentionally deliberate. I approach AI tools as an accelerator for my own ideas and as an extension to my skills, which I have developed for decades. Everything here I _could_ have written 100% from scratch. But that is not the world we live in any longer.

I run local AI on two clustered NVidia DGX Spark boxes, and Qwen 3.6-27B-FP8 with full-precision KV cache is my daily driver. It's amazing and hits _way_ above its weight class. Additionally, I leveraged GLM 5.2 in Ollama cloud for some of the heavier lifting. IMO GLM 5.2 is the best option out there for the money. Don't believe me? Write something with Opus or GPT 5.6 Sol and have GLM 5.2 perform a review. Hell, have _Qwen 3.6 27B_ do a review. Then give the review feedback back to the "frontier" models.

So, I don't care where your code came from. If you submit a PR you need to understand it and be able to answer any questions about the how/why intelligently and without just regurgitating Chat GPT output. I think that is reasonable. If you're a 100% vibe coder and have never actually done this for a living (or at least a serious hobby!) please skip contributing code, but I'd still love to hear any new feature ideas or bug reports you may have.

So, please no 100% AI generated, AI submitted PRs where you never looked at the code. I don't care what anyone else says - looking _still_ matters.

## Build and test

```powershell
dotnet build src\Threadsmith.sln
dotnet test --solution src\Threadsmith.sln
```

For a lower-overhead interactive run:

```powershell
dotnet run --configuration Release --project src\Threadsmith.App -- --tui
```

### Local Publish

Local publish (Windows Example):
```powershell
   dotnet publish .\src\Threadsmith.App\Threadsmith.App.csproj `
       -c Release `
       -r win-x64 `
       --self-contained true `
       -p:DebugType=none `
       -p:DebugSymbols=false `
       -o "C:\Program Files\Threadsmith"
```
Note that Windows protects the Program Files folder, and your shell will need to be an administrator if you want to publish to that location. Otherwise, you can change the publish target to suit your preference. `PublishSingleFile` is intentionally not used because Threadsmith loads extensions via `AssemblyLoadContext` and spawns the scripting worker as a separate process, both of which require separate assembly files.

**Note:** Eventually, I hope to have release builds set up and run in Github. For now, anyone who wants to use it must build and run locally. I have not set up Github for an actual public repo before, so if anyone has experience and wants to help out, my answer is "yes".

## Project layout

```text
Threadsmith/
├── src/
│   ├── Threadsmith.App/                  # composition root and executable host
│   ├── Threadsmith.Core/                 # commands, events, projections, policy-neutral contracts
│   ├── Threadsmith.Execution/            # governed turn and run orchestration
│   ├── Threadsmith.Context/              # evidence, conversation memory, prompts, and model resolution
│   ├── Threadsmith.Models/               # provider-neutral model contracts and adapters
│   ├── Threadsmith.Tools/                # tool contracts, registry, policy, and built-ins
│   ├── Threadsmith.DotNet/               # Roslyn/MSBuild discovery and semantic operations
│   ├── Threadsmith.Workspaces/            # repository lifecycle and transactional mutation
│   ├── Threadsmith.Validation/            # build, diagnostic, and test validation
│   ├── Threadsmith.Persistence/           # durable events, facts, artifacts, and migrations
│   ├── Threadsmith.Telemetry/             # metrics, tracing, logging, and diagnostics
│   ├── Threadsmith.Tui/                   # conversation-first terminal adapter
│   ├── Threadsmith.Cli/                   # headless command adapter
│   ├── Threadsmith.Mcp/                   # host-owned MCP boundary
│   ├── Threadsmith.Skills/                # governed declarative catalog and workflows
│   └── Threadsmith.Extensions.*/          # extension contracts and runtime
├── tests/                                 # architecture and scope-focused verification
├── docs/                                  # user, architecture, operations, and planning docs
└── .threadsmith/config.example             # annotated repository configuration schema
```

### DOX Framework

Not so much a "framework" but an addition to AGENTS.MD - this project uses the process described in https://github.com/agent0ai/dox to help provide localized (in the project) details to AI agents. I think it's been helpful, and using it is nothing more than a block on AGENTS.md - the additional, localized documentation in the sub-AGENTS.md files is useful for humans, too!

## Documentation

The documentation folder contains user and developer docs, as well as implementation milestones, requirerments, checklists, plans, etc. I have made a best-effort to keep everything aligned and in sync with the actual state of the code. If you find any gaps or inconsistencies, please let me know or submit a PR with the correction.

- **[User guide](docs/user-guide.md)** — installation, repository onboarding, trust, commands, governed changes, tools, models, configuration, extensions, automation, safety, and troubleshooting.
- [Contributing guide](CONTRIBUTING.md) — local setup, coding standards, tests, commits, and pull-request expectations.
- [Repository configuration example](.threadsmith/config.example) — complete annotated configuration schema.
- [Operations references](docs/operations/) — focused command, conversation-context, provider, tool, theme, and repository workflows.
- [Conversation context operations](docs/operations/conversation-context.md) — modes, `/context`, inspection, compaction, configuration, retention, and restoration.
- [Parallel-agent operations](docs/operations/parallel-agents.md) — delegation limits, `/agents`, worktree isolation, cancellation, conflicts, and recovery.
- [Governed skills operations](docs/operations/skills.md) — catalogs, verification, enablement, `/skills`, invocation, workflows, and recovery.
- [Declarative skill authoring](docs/skill-authoring.md) — package layout, manifests, safe schemas, workflows, signing, import, and testing.
- [Extension authoring guide](docs/extension-authoring/authoring-guide.md) — stable extension contracts and lifecycle guidance.
- [Architecture decisions](docs/architecture/) — ADRs and subsystem contracts.
- [Implementation roadmap](docs/implementation-plans/README.md) — sequenced plans, milestones, acceptance scenarios, and manual verification.
- [Milestones](docs/implementation-plans/milestones.md)
- [C# guardrails](docs/guardrails/portable-csharp-guardrails.md) — required repository coding standards.
- [AGENTS.md](AGENTS.md) — behavioral contract for coding agents working in this repository.

## License

Threadsmith.NET is open source under the [Apache License 2.0](LICENSE).

### As-is
_Threadsmith.NET is provided on an “AS IS” basis, without warranties or conditions of any kind. It can execute tools and modify repository files when authorized. Review proposed changes, maintain backups, and use appropriate repository permissions. See the [Apache License 2.0](LICENSE) for the applicable warranty disclaimer and limitation of liability._
