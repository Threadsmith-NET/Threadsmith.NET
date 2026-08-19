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

Threadsmith is a governed coding harness: the host owns trust, approval, mutation, validation, and durable-state boundaries. Proposals should preserve those boundaries and the dependency rules documented in [AGENTS.md](AGENTS.md).

## Local development

### Prerequisites

- Git.
- The .NET 10 SDK selected by [`global.json`](global.json) (currently `10.0.204`, with latest-feature roll-forward).
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
   dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build
   ```

5. Create a focused branch:

   ```powershell
   git switch -c feature/short-description
   # or
   git switch -c fix/short-description
   ```

## Repository standards

Before changing files:

1. Read the root [AGENTS.md](AGENTS.md).
2. Follow its DOX chain by reading every closer `AGENTS.md` that owns the files you will touch.
3. Before writing or modifying C#, read the [portable C# guardrails](docs/guardrails/portable-csharp-guardrails.md).
4. Review applicable architecture decisions and implementation plans under [`docs/`](docs/).

Important repository conventions include:

- Target .NET 10 and the repository's latest C# language version.
- Keep nullable analysis clean; do not use null-forgiving suppression.
- Treat warnings and enabled analyzer findings as errors.
- Add external package versions to [`Directory.Packages.props`](Directory.Packages.props), not individual project files.
- Preserve dependency direction; architecture tests enforce subsystem boundaries.
- Propagate cancellation through asynchronous boundaries.
- Add XML documentation to public members.
- Prefer small, focused changes and avoid unrelated formatting or refactoring.
- Add or update meaningful tests for externally observable behavior changes.
- Complete the required DOX pass after meaningful changes so owning `AGENTS.md` files and child indexes remain current.

The root [`.editorconfig`](.editorconfig) owns formatting and style. Check formatting without rewriting unrelated files:

```powershell
dotnet format src/Threadsmith.sln --verify-no-changes --no-restore
```

If formatting must be applied, limit it to the files or projects involved in your change and review the resulting diff carefully.

## Testing changes

Threadsmith uses xUnit v3 with Microsoft.Testing.Platform. At minimum, run the focused test project covering your change. Before submitting a pull request, run the same product build and test commands used by CI:

```powershell
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build
```

Also run checks owned by the area you changed:

- Architecture or project-reference changes: `tests/Threadsmith.Architecture.Tests`.
- Release automation changes: follow [`eng/AGENTS.md`](eng/AGENTS.md) and run the release contract checks.
- Spike changes: build `spikes/Spikes.sln` and run the affected headless-safe spike.
- Interactive terminal changes: update automated projection tests and the maintained [manual test plan](docs/implementation-plans/manual-test-plan.md) when real-terminal behavior changes.

If a relevant check cannot be run locally, explain why in the pull request and identify the check that remains outstanding.

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
6. Update documentation, tests, and the applicable DOX files when behavior or durable contracts change.
7. Ensure the Windows, Linux, and macOS GitHub Actions checks pass.

Maintainers may request changes to preserve host authority, repository containment, public contracts, cross-platform behavior, terminal compatibility, or test quality.

## License

By contributing, you agree that your contribution will be licensed under the repository's [Apache License 2.0](LICENSE).
