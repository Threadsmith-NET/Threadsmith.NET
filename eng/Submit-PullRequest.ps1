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
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE. PR validation did not complete." }
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
$head = if ($sourceRepository -eq $TargetRepository) { $branch } else { "$($sourceRepository.Split('/')[0]):$branch" }
$existing = @((Invoke-Checked gh @('pr', 'list', '--repo', $TargetRepository, '--head', $head, '--base', $Base,
    '--state', 'open', '--json', 'number,url') | Out-String) | ConvertFrom-Json)
if ($existing.Count -gt 1) { throw 'More than one matching PR was found.' }
if ($ValidateOnly -and $existing.Count -eq 0) { throw 'No matching open PR exists to validate.' }

# Opening the PR (or pushing to an existing PR) triggers the sole CI run.
# Never dispatch another workflow for the same submission.
Invoke-Checked pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'release/Test-SourceDocumentation.ps1')) | Out-Host
Invoke-Checked git @('push', '--set-upstream', 'origin', $branch) | Out-Host
if ($existing.Count -gt 0) {
    $prUrl = $existing[0].url
    $prNumber = $existing[0].number
} else {
    $prUrl = (Invoke-Checked gh @('pr', 'create', '--repo', $TargetRepository, '--head', $head, '--base', $Base,
        '--title', $Title, '--body-file', $BodyFile) | Out-String).Trim()
    $prNumber = (Invoke-Checked gh @('pr', 'view', $prUrl, '--json', 'number', '--jq', '.number')).Trim()
}
Write-Output $prUrl

$run = $null
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    $runs = (Invoke-Checked gh @('run', 'list', '--repo', $TargetRepository, '--workflow', 'build.yml', '--branch', $branch,
        '--event', 'pull_request', '--commit', $revision, '--limit', '20', '--json', 'databaseId,headSha,createdAt') | Out-String) | ConvertFrom-Json
    foreach ($candidate in @($runs | Where-Object { $_.headSha -eq $revision } | Sort-Object createdAt -Descending)) {
        $pullRequests = @(Invoke-Checked gh @('api', "repos/$TargetRepository/actions/runs/$($candidate.databaseId)", '--jq', '.pull_requests[].number'))
        if ($pullRequests -contains [string]$prNumber) { $run = $candidate; break }
    }
    if ($run) { break }
    Start-Sleep -Seconds 5
}
if (-not $run) { throw "No PR-triggered CI run was found for this revision. PR remains open: $prUrl" }
Invoke-Checked gh @('run', 'watch', [string]$run.databaseId, '--repo', $TargetRepository, '--exit-status', '--interval', '15') | Out-Host
$evidence = (Invoke-Checked gh @('run', 'view', [string]$run.databaseId, '--repo', $TargetRepository,
    '--json', 'headSha,status,conclusion,jobs,url') | Out-String) | ConvertFrom-Json
if ($evidence.headSha -ne $revision -or $evidence.status -ne 'completed' -or $evidence.conclusion -ne 'success') {
    throw "CI did not pass for the exact submitted revision. PR remains open: $prUrl"
}
foreach ($platform in @('ubuntu-latest', 'windows-latest', 'macos-latest')) {
    $job = @($evidence.jobs | Where-Object { $_.name -eq "build & test ($platform)" })
    if ($job.Count -ne 1 -or $job[0].conclusion -ne 'success') {
        throw "Required platform $platform did not pass. PR remains open: $prUrl"
    }
}
if ((Invoke-Checked git @('rev-parse', 'HEAD')).Trim() -ne $revision -or (Invoke-Checked git @('status', '--porcelain'))) {
    throw 'The checkout changed during validation.'
}
$remoteHead = (Invoke-Checked git @('ls-remote', 'origin', "refs/heads/$branch") | Out-String).Trim().Split()[0]
if ($remoteHead -ne $revision) { throw 'The remote branch changed during validation.' }
$prHead = (Invoke-Checked gh @('pr', 'view', $prUrl, '--json', 'headRefOid', '--jq', '.headRefOid')).Trim()
if ($prHead -ne $revision) { throw 'The PR head changed during validation.' }
Write-Host "All three CI platforms passed for ${revision}: $($evidence.url)"
