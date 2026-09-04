# Opening a Repository

Threadsmith.NET opens repositories through application commands; repository discovery does not require a model.

## Interactive terminal

Launch from the intended repository and omit `--repository`; the current directory is used automatically:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --tui
```

Use `--repository <path>` to start elsewhere, and optionally supply `--trust <level>` and `--solution <path>`. Without those explicit values, trust and ambiguous solutions appear as numbered highlighted lists; use Up/Down and Enter. `/open [path]` switches repositories after startup, and `/trust [inspect|read|build|mutation]` changes the active grant. For a repository without persisted trust, choose one trust option:

- **Trusted Read** is the default. It permits file inspection, solution/project selection, declared TFM inventory, and baseline hashing. It does not run restore, builds, analyzers, source generators, or tests.
- **Trusted Build** permits restore, MSBuild evaluation, analyzers/source generators, compilation, and approved build/process tools. Repository-controlled code may execute, so grant it only to repositories you trust.
- **Trusted Mutation** includes build trust and permits explicitly approved file mutations under configured approved roots. Staging remains private and write-free; each commit is hash- and path-checked.
- **Inspect Only** safely loads `.threadsmith/config.json` and lists solution/project candidates without reading their content.

Previously persisted `TrustedBuild` or higher trust is reused on reopen from the per-user repository-facts database. Persisted `TrustedRead` opens an upgrade prompt where **Keep Trusted Read** retains text-only discovery, **Upgrade to Build** enables compiler-aware discovery, and **Cancel** leaves the repository closed. Trust is monotonic: requesting a lower level does not erase a persisted higher grant. The configured `solution:path` remains the first candidate. Discovery recursively finds solution containers below the repository while skipping `.git`, `bin`, `obj`, reparse points, and unreadable/reserved-name entries. A single candidate is selected automatically; multiple candidates require explicit selection. Repository, solution, and semantic-confidence results append to the existing conversation.

`TrustedRead` intentionally produces `TextOnly` semantic confidence because it does not evaluate MSBuild. Choose `TrustedBuild` initially or upgrade a persisted read grant when compiler-backed evaluation is required.

After a solution is selected, Threadsmith monitors relevant confined repository changes and refreshes the shared semantic workspace after a bounded settling interval. Requests wait for pending refresh before a model run can start. Use `/semantic_refresh` to force and await a complete refresh without invoking the model; see [Semantic refresh](semantic-refresh.md) for incremental/full classification, background output, headless parity, and recovery.

## Headless CLI

Inspect candidates without granting read trust:

```powershell
dotnet run --project src/Threadsmith.App -- --repository C:\source\my-repo
```

When the process current directory is the repository, omit `--repository`. With no positional request, the current directory is inspected:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --trust TrustedRead --solution src\MyRepo.sln
```

Open with read trust and select a solution explicitly:

```powershell
dotnet run --project src/Threadsmith.App -- --repository C:\source\my-repo --trust TrustedRead --solution src\MyRepo.sln
```

The supported trust names are `UntrustedInspection`, `TrustedRead`, `TrustedBuild`, `TrustedMutation`, and `FullyTrustedAutomation`. When trusted headless discovery finds multiple candidates without `--solution`, it lists them and exits `2` instead of choosing arbitrarily. `TrustedBuild` or above permits an explicit `dotnet restore` during solution selection; restore failure is recorded for semantic-confidence degradation rather than hidden.

## Safety Boundary

- Configured, selected, and solution-referenced paths must stay inside the normalized repository root. Every existing component beneath that root is checked, so a symbolic-link or junction ancestor cannot redirect a read outside it.
- Repository roots and approved baseline roots cannot be symbolic links or junctions; nested reparse points, inaccessible entries, and Windows reserved-name entries encountered during discovery and baseline enumeration are skipped and counted by workspace telemetry.
- Baselines include source and project/build metadata beneath configured `editableRoots`, excluding `.git`, `bin`, `obj`, prohibited paths, and reparse points.
- `prohibitedPaths` uses slash-normalized glob syntax: `*` and `?` stay within one path segment, `**` spans directory boundaries, and a trailing `/` excludes all descendants.
- Repository configuration is treated as data and is never executed.
- Filesystem notifications do not grant read authority. Refresh revalidates confinement, prohibited paths, and reparse-point policy before reading a candidate.
