# Contributing to Threadsmith.NET

Thank you for helping improve Threadsmith.NET. Code contributions, documentation updates, bug reports, and focused feature proposals are welcome.

Please read these guidelines before opening an issue or pull request. By participating, you agree to follow the repository's [Code of Conduct](CODE_OF_CONDUCT.md).

## Code of Conduct

Threadsmith.NET has adopted the Contributor Covenant. Contributors, maintainers, and community participants must follow the [Code of Conduct](CODE_OF_CONDUCT.md) in project spaces and when representing the project. Report unacceptable behavior through the enforcement contact documented there.

## Report a bug or propose a feature

1. Search the [existing issues](https://github.com/Threadsmith-NET/Threadsmith.NET/issues) before opening a new one.
2. For a bug, include reproducible steps, expected and actual behavior, relevant sanitized logs, operating system, terminal, and the output of `dotnet --version`.
3. For a feature, explain the user problem, the proposed behavior, important safety or compatibility constraints, and any alternatives considered.
4. Never include credentials, access tokens, private repository content, or other secrets in an issue, log, fixture, or screenshot.

Threadsmith is a governed coding harness: the host owns trust, approval, mutation, validation, and durable-state boundaries. Proposals should preserve those boundaries and the dependency rules documented in the source repository's [AGENTS.md](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/AGENTS.md).

## Local development

### Prerequisites

- Git.
- The .NET 10 SDK selected by [`global.json`](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/global.json) (currently `10.0.204`, with latest-feature roll-forward).
- PowerShell for repository-maintained release scripts. Normal restore, build, and test commands are cross-platform.
- An editor or IDE with current .NET and C# support.

Verify the selected SDK:

```powershell
dotnet --version
```

### Set up your fork

1. Fork [Threadsmith.NET](https://github.com/Threadsmith-NET/Threadsmith.NET) on GitHub.
2. Clone your fork and enter the repository:

   ```powershell
   git clone https://github.com/YOUR-ACCOUNT/Threadsmith.NET.git
   cd Threadsmith.NET
   ```

3. Restore and build the product solution:

   ```powershell
   dotnet restore src/Threadsmith.sln
   dotnet build src/Threadsmith.sln --configuration Debug --no-restore
   ```

   For app-local ripgrep acceleration during source-development runs, stage the pinned asset once for the current RID, then rebuild the App:

   ```powershell
   pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1
   dotnet build src/Threadsmith.App/Threadsmith.App.csproj --no-restore
   ```

   The staging script verifies the release-owned SHA-256 manifest and writes only to ignored `artifacts/dev-tools/<rid>`; ordinary builds remain offline. Pass `-ArchivePath <path>` for an already-downloaded official archive.

4. Run the test suite:

   ```powershell
   dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
   ```

5. Create a focused branch:

   ```powershell
   git switch -c feature/short-description
   # or
   git switch -c fix/short-description
   ```

## Repository standards

Before changing files:

1. Read the root [AGENTS.md](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/AGENTS.md).
2. Before writing or modifying C#, read the [portable C# guardrails](docs/guardrails/portable-csharp-guardrails.md).
3. Review applicable architecture decisions and implementation plans from the [documentation index](docs/index.md).

Important repository conventions include:

- Target .NET 10 and the repository's latest C# language version.
- Follow G-1 for nullable analysis and the test-project null-suppression exception.
- Treat warnings and enabled analyzer findings as errors.
- Add external package versions to [`Directory.Packages.props`](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/Directory.Packages.props), not individual project files.
- Preserve dependency direction; architecture tests enforce subsystem boundaries.
- Propagate cancellation through asynchronous boundaries.
- Follow G-18 for XML documentation and the test/spike exemptions.
- Prefer small, focused changes and avoid unrelated formatting or refactoring.
- Add or update meaningful tests for externally observable behavior changes.

The root [`.editorconfig`](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/.editorconfig) owns formatting and style. Check formatting without rewriting unrelated files:

```powershell
dotnet format src/Threadsmith.sln --verify-no-changes --no-restore
```

If formatting must be applied, limit it to the files or projects involved in your change and review the resulting diff carefully.

Visual Studio also displays suggestion-level analyzer diagnostics. A build summary with zero warnings and errors does not include those suggestions. Check them explicitly when verifying changed code:

```powershell
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info
```

Use `--include` with the changed file paths to focus this check. Correct relevant findings in the code; do not disable their rules to obtain a clean result.

## Testing changes

Threadsmith uses xUnit v3 with Microsoft.Testing.Platform. At minimum, run the focused test project covering your change. Before submitting a pull request, run the same product build and test commands used by CI:

```powershell
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
```

Also run checks owned by the area you changed:

- Architecture or project-reference changes: `tests/Threadsmith.Architecture.Tests`.
- Release automation changes: run the relevant release contract checks under `eng/release/`.
- Interactive terminal changes: update automated projection tests and the maintained [manual test plan](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/manual-test-plan.md) when real-terminal behavior changes.

If a relevant check cannot be run locally, explain why in the pull request and identify the check that remains outstanding.

`Threadsmith.NativeTools.Tests` runs independent test fixtures sequentially because the pinned Roslyn 5.6 dependency [shares one in-memory SQLite write cache across workspaces on non-Windows platforms](https://github.com/dotnet/roslyn/blob/c0573ed0a7dc3e3b4d2e70da47f97cc51a35524f/src/Workspaces/Core/Portable/Storage/SQLite/v2/Interop/SqlConnection.cs), while scheduling writes per workspace. Concurrency exercised explicitly inside a test remains enabled. Other test assemblies can still run in parallel.

## Commits

Use concise, imperative commit subjects that describe the outcome, for example:

- `Add repository selection guidance`
- `Fix tool activity credential redaction`
- `docs: clarify provider timeout configuration`

Keep commits reviewable and avoid mixing unrelated work. Conventional prefixes such as `feat:`, `fix:`, and `docs:` are welcome but are not required.

## Submit a pull request

1. Review the complete diff and remove unrelated changes, generated files, logs, credentials, and local configuration.
2. Ensure the solution builds cleanly and relevant automated tests pass.
3. Push your branch to your fork and open a pull request against `main`.
4. Explain the problem, solution, important design choices, risks, and verification performed.
5. Link the tracking issue when applicable (for example, `Closes #42`).
6. Update documentation and tests when behavior or durable contracts change.
7. Ensure the Windows, Linux, and macOS GitHub Actions checks pass.

Maintainers may request changes to preserve host authority, repository containment, public contracts, cross-platform behavior, terminal compatibility, or test quality.

## License

By contributing, you agree that your contribution will be licensed under the repository's [Apache License 2.0](LICENSE).
