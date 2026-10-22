[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Create')][string] $Title,
    [Parameter(Mandatory, ParameterSetName = 'Create')][string] $BodyFile,
    [Parameter(Mandatory, ParameterSetName = 'Validate')][switch] $ValidateOnly,
    [string] $TargetRepository,
    [string] $Base = 'main'
)
$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param([string] $Command, [string[]] $Arguments)
    $result = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE. No PR was opened." }
    return $result
}

$root = (Invoke-Checked git @('rev-parse', '--show-toplevel')).Trim()
if ([IO.Path]::GetFullPath($root) -ne [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) {
    throw 'Run this script from its own active checkout.'
}
if (Invoke-Checked git @('status', '--porcelain')) { throw 'Commit the intended changes first; the checkout must be clean.' }
$branch = (Invoke-Checked git @('branch', '--show-current')).Trim()
if (-not $branch -or $branch -eq $Base) { throw 'Use a feature branch, not a detached HEAD or the base branch.' }
$revision = (Invoke-Checked git @('rev-parse', 'HEAD')).Trim()
$remote = (Invoke-Checked git @('remote', 'get-url', 'origin')).Trim()
$sourceRepository = (Invoke-Checked gh @('repo', 'view', $remote, '--json', 'nameWithOwner', '--jq', '.nameWithOwner')).Trim()
if (-not $TargetRepository) { $TargetRepository = $sourceRepository }
if (-not $ValidateOnly) { $BodyFile = (Resolve-Path -LiteralPath $BodyFile).Path }

# Run the same packaging preflight as CI. Cross-platform builds/tests are delegated
# to the existing workflow, never to a second implementation of its checks.
Invoke-Checked pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'release/Test-SourceDocumentation.ps1')) | Out-Host
Invoke-Checked git @('push', '--set-upstream', 'origin', $branch) | Out-Host
$started = [DateTimeOffset]::UtcNow.AddSeconds(-5)
Invoke-Checked gh @('workflow', 'run', 'build.yml', '--repo', $sourceRepository, '--ref', $branch) | Out-Host

$run = $null
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    $runs = (Invoke-Checked gh @('run', 'list', '--repo', $sourceRepository, '--workflow', 'build.yml', '--branch', $branch,
        '--event', 'workflow_dispatch', '--limit', '20', '--json', 'databaseId,headSha,createdAt') | Out-String) | ConvertFrom-Json
    $run = $runs | Where-Object { $_.headSha -eq $revision -and [DateTimeOffset]::Parse($_.createdAt) -ge $started } |
        Sort-Object createdAt -Descending | Select-Object -First 1
    if ($run) { break }
    Start-Sleep -Seconds 5
}
if (-not $run) { throw 'Could not identify the dispatched CI run for this revision. No PR was opened.' }
Invoke-Checked gh @('run', 'watch', [string]$run.databaseId, '--repo', $sourceRepository, '--exit-status', '--interval', '15') | Out-Host
$evidence = (Invoke-Checked gh @('run', 'view', [string]$run.databaseId, '--repo', $sourceRepository,
    '--json', 'headSha,status,conclusion,jobs,url') | Out-String) | ConvertFrom-Json
if ($evidence.headSha -ne $revision -or $evidence.status -ne 'completed' -or $evidence.conclusion -ne 'success') {
    throw 'CI did not pass for the exact submitted revision. No PR was opened.'
}
foreach ($platform in @('ubuntu-latest', 'windows-latest', 'macos-latest')) {
    $job = @($evidence.jobs | Where-Object { $_.name -eq "build & test ($platform)" })
    if ($job.Count -ne 1 -or $job[0].conclusion -ne 'success') {
        throw "Required platform $platform did not pass. No PR was opened."
    }
}
if ((Invoke-Checked git @('rev-parse', 'HEAD')).Trim() -ne $revision -or (Invoke-Checked git @('status', '--porcelain'))) {
    throw 'The checkout changed during validation. No PR was opened.'
}
$remoteHead = (Invoke-Checked git @('ls-remote', 'origin', "refs/heads/$branch") | Out-String).Trim().Split()[0]
if ($remoteHead -ne $revision) { throw 'The remote branch changed during validation. No PR was opened.' }
Write-Host "All three CI platforms passed for ${revision}: $($evidence.url)"
if ($ValidateOnly) { return }

$head = if ($sourceRepository -eq $TargetRepository) { $branch } else { "$($sourceRepository.Split('/')[0]):$branch" }
$existing = (Invoke-Checked gh @('pr', 'list', '--repo', $TargetRepository, '--head', $head, '--base', $Base,
    '--state', 'open', '--json', 'url') | Out-String) | ConvertFrom-Json
if (@($existing).Count -gt 0) {
    $existing | ForEach-Object { Write-Output $_.url }
    return
}
Invoke-Checked gh @('pr', 'create', '--repo', $TargetRepository, '--head', $head, '--base', $Base, '--title', $Title, '--body-file', $BodyFile)
