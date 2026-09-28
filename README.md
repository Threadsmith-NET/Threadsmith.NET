# Threadsmith.NET

A .NET-native, terminal-first coding harness that treats C# code as code, not just text.

Threadsmith.NET opens real .NET repositories with Roslyn and MSBuild, gives models governed tools backed by compiler semantics, requires host-validated plans before repository changes, stages mutations transactionally, and performs semantic checks before the project is built. Interactive use defaults to a full-screen TUIKit interface; the same application workflows are available headlessly for scripts and CI.

Threadsmith.NET is currently in beta and under active testing and refinement. For current implementation status, see the [milestone plan](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/milestones.md).

## Why Threadsmith?

Many coding agents treat a repository primarily as text and depend on repeated edit/build/test cycles to discover whether a change was valid. Threadsmith combines model reasoning with host-owned discovery, governed mutation, and targeted validation. When a repository is trusted for build execution, compiler-backed evidence improves navigation, impact analysis, refactoring, and early detection of syntax or binding problems. A text-only path remains available when semantic evaluation is unavailable, unnecessary, or inappropriate.

Threadsmith is designed for unfamiliar or large C# solutions, multi-project changes, refactoring, API evolution, and work where dependencies or overloads make text-only editing unreliable. Its core principles are:

- **Compiler-aware evidence** — Roslyn and MSBuild provide symbol, reference, caller, project, and diagnostic context.
- **Human-governed changes** — models cannot authorize their own plans or mutations.
- **Transactional mutation** — proposed changes are scope-checked, staged, shown as exact diffs, approved under host policy, applied transactionally, and validated.
- **Host-owned control flow** — trust, policy, approvals, containment, cancellation, and validation remain outside the model.
- **Interactive/headless parity** — the terminal and automation adapters project the same application commands and state.
- **Replaceable integrations** — model providers, extensions, MCP, Roslyn, and terminal libraries remain behind host-owned contracts.

Threadsmith is a governed execution environment, not a general operating-system sandbox. Grant repository trust deliberately and use the minimum level required for the task. The [user guide explains each trust level](docs/user-guide.md#trust-levels), and the [safety model](docs/user-guide.md#safety-model) describes the enforced boundaries and limitations.

## Who is Threadsmith for?

Short answer: me—and, I hope, other developers who value .NET, strong typing, and compiler-aware agentic engineering. The project grew from the belief that better structure and better evidence can help agents produce cleaner code with fewer avoidable runtime and integration errors. The approach is especially relevant to substantial C# codebases, but its underlying ideas can apply to other languages and toolchains too.

## Requirements

- .NET 10 SDK when building from source.
- PowerShell for the examples below. Bash and Git Bash should also work, although every combination has not been tested.

Self-contained release artifacts include the matching .NET runtime. See [release installation and verification](docs/operations/release-packaging.md) for supported packages and platform-specific instructions.

## Quick start

Restore and build the repository:

```powershell
dotnet restore src\Threadsmith.sln
dotnet build src\Threadsmith.sln
```

From the repository you want to inspect, launch the interactive terminal:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --tui
```

When running Threadsmith against its own source tree, use:

```powershell
dotnet run --project src\Threadsmith.App -- --tui
```

For a headless request:

```powershell
dotnet run --project src\Threadsmith.App -- "inspect this repository"
```

The current directory is the default repository. Interactive startup guides trust and ambiguous solution selection. For repository and solution arguments, compiled-application usage, terminal commands, and configuration, continue with the [user guide](docs/user-guide.md#starting-threadsmith).

## Releases
Packaged releases are available on the [releases](https://github.com/Threadsmith-NET/Threadsmith.NET/releases) page. Note that the latest release is not necessarily the latest commit on the main branch. The main branch is under active development and may be unstable.

## Capabilities

Threadsmith provides governed repository inspection, local Git evidence, .NET inventory and validation, Roslyn-backed code exploration, transactional changes, resumable sessions, parallel tools and agents, model-provider selection, declarative skills, lifecycle hooks, extensions, MCP connections, and optional web, pull-request, and Jira integrations.

Tool availability is determined by repository trust, configuration, policy, semantic-workspace state, and approval. Loaded extensions and configured MCP servers can contribute capabilities through the same host-owned policy pipeline. See:

- [Tools and tool availability](docs/user-guide.md#tools-and-tool-availability)
- [How repository changes are governed](docs/user-guide.md#how-repository-changes-are-governed)
- [Governed skills and reusable workflows](docs/user-guide.md#governed-skills-and-reusable-workflows)
- [Model providers, secrets, and reasoning](docs/user-guide.md#model-providers-secrets-and-reasoning)
- [MCP connection profiles](docs/user-guide.md#mcp-connection-profiles)

## Project layout

```text
Threadsmith/
├── src/
│   ├── Threadsmith.App/                  # composition root and executable host
│   ├── Threadsmith.Core/                 # commands, events, projections, and core contracts
│   ├── Threadsmith.Execution/            # governed turn and run orchestration
│   ├── Threadsmith.Context/              # evidence, conversation context, prompts, and model resolution
│   ├── Threadsmith.Models/               # provider-neutral model contracts and adapters
│   ├── Threadsmith.Tools/                # tool registry, policy, and built-ins
│   ├── Threadsmith.DotNet/               # Roslyn/MSBuild discovery and semantic operations
│   ├── Threadsmith.Workspaces/            # repository lifecycle and transactional mutation
│   ├── Threadsmith.Validation/            # build, diagnostic, and test validation
│   ├── Threadsmith.Persistence/           # durable events, artifacts, and migrations
│   ├── Threadsmith.Interaction/           # frontend-neutral interactive coordination
│   ├── Threadsmith.Tui.TuiKit/            # default terminal frontend
│   └── Threadsmith.Extensions.*/          # extension contracts and runtime
├── tests/                                 # architecture and behavior verification
├── docs/                                  # user, operations, authoring, and architecture documentation
└── .threadsmith/config.example             # annotated repository configuration schema
```

## Contributing

Bug reports, feature proposals, code changes, and documentation improvements are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) for setup, coding standards, testing expectations, the policy for AI-assisted contributions, and the pull-request checklist. Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

Areas where additional testing is especially useful include provider authentication, MCP and SSO flows, cross-platform terminal behavior, and installer verification—particularly on macOS.

## Documentation

- **[User guide](docs/user-guide.md)** — installation, repository onboarding, trust, commands, governed changes, tools, models, configuration, automation, safety, and troubleshooting.
- [Documentation index](docs/index.md) — operations, authoring, architecture, testing, and guardrail references.
- [Operations references](docs/operations/README.md) — focused subsystem configuration and workflows.
- [Contributing guide](CONTRIBUTING.md) — development setup, repository standards, tests, commits, and pull requests.
- [Architecture decisions](docs/architecture/README.md) — accepted decisions and subsystem contracts.

Implementation plans and release-readiness material can describe future or provisional behavior and are therefore maintained separately from installed product documentation.

## License

Threadsmith.NET is licensed under the [Apache License 2.0](LICENSE) and is provided on an “AS IS” basis, without warranties or conditions of any kind.
