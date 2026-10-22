[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

# Command doubles keep this contract test offline: no push, workflow dispatch,
# or PR creation may reach the real git/gh executables.
$global:PrSubmissionTest = $null
function git {
    $global:LASTEXITCODE = 0
    switch ($args -join ' ') {
        'rev-parse --show-toplevel' { return $global:PrSubmissionTest.Root }
        'status --porcelain' { if ($global:PrSubmissionTest.Mode -eq 'dirty') { return ' M changed.cs' }; return }
        'branch --show-current' { return 'feature/test' }
        'rev-parse HEAD' { return 'abc123' }
        'remote get-url origin' { return 'https://github.com/example/repo.git' }
        'push --set-upstream origin feature/test' { $global:PrSubmissionTest.Pushed = $true; return }
        'ls-remote origin refs/heads/feature/test' {
            $sha = if ($global:PrSubmissionTest.Mode -eq 'remote-changed') { 'other' } else { 'abc123' }
            return "$sha`trefs/heads/feature/test"
        }
        default { throw "Unexpected git call: $args" }
    }
}
function pwsh {
    $global:PrSubmissionTest.Preflight = $true
    $global:LASTEXITCODE = if ($global:PrSubmissionTest.Mode -eq 'preflight-failed') { 1 } else { 0 }
}
function gh {
    $global:LASTEXITCODE = 0
    switch -Regex ($args -join ' ') {
        '^repo view ' { return 'example/repo' }
        '^workflow run ' { $global:PrSubmissionTest.Dispatched = $true; return }
        '^run list ' { return ConvertTo-Json -InputObject @(@{ databaseId = 42; headSha = 'abc123'; createdAt = [DateTimeOffset]::UtcNow.ToString('o') }) }
        '^run watch ' { if ($global:PrSubmissionTest.Mode -eq 'failed') { $global:LASTEXITCODE = 1 }; return }
        '^run view ' {
            $jobs = @('ubuntu-latest', 'windows-latest', 'macos-latest') | ForEach-Object {
                if ($global:PrSubmissionTest.Mode -ne 'missing-platform' -or $_ -ne 'macos-latest') {
                    @{ name = "build & test ($_)"; conclusion = $(if ($global:PrSubmissionTest.Mode -eq 'skipped-platform' -and $_ -eq 'macos-latest') { 'skipped' } else { 'success' }) }
                }
            }
            return @{ headSha = $(if ($global:PrSubmissionTest.Mode -eq 'wrong-revision') { 'other' } else { 'abc123' }); status = 'completed'; conclusion = 'success'; jobs = @($jobs); url = 'https://example.test/run/42' } | ConvertTo-Json -Depth 5
        }
        '^pr list ' { return '[]' }
        '^pr create ' { $global:PrSubmissionTest.Created++; return 'https://example.test/pr/1' }
        default { throw "Unexpected gh call: $args" }
    }
}

try {
    foreach ($mode in @('success', 'validate-only', 'dirty', 'preflight-failed', 'failed', 'wrong-revision', 'missing-platform', 'skipped-platform', 'remote-changed')) {
        $global:PrSubmissionTest = @{ Mode = $mode; Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')); Preflight = $false; Pushed = $false; Dispatched = $false; Created = 0 }
        $failure = $null
        try {
            if ($mode -eq 'validate-only') {
                & (Join-Path $PSScriptRoot 'Submit-PullRequest.ps1') -ValidateOnly | Out-Null
            } else {
                & (Join-Path $PSScriptRoot 'Submit-PullRequest.ps1') -Title 'Test' -BodyFile $PSCommandPath | Out-Null
            }
        } catch { $failure = $_ }
        $shouldPass = $mode -in @('success', 'validate-only')
        if ($shouldPass -and $failure) { throw "${mode}: unexpected failure: $failure" }
        if (-not $shouldPass -and -not $failure) { throw "${mode}: the gate did not reject invalid evidence." }
        $expectedCreates = if ($mode -eq 'success') { 1 } else { 0 }
        if ($global:PrSubmissionTest.Created -ne $expectedCreates) { throw "${mode}: incorrect PR creation count." }
        if ($shouldPass -and (-not $global:PrSubmissionTest.Preflight -or -not $global:PrSubmissionTest.Pushed -or -not $global:PrSubmissionTest.Dispatched)) {
            throw "${mode}: a required gate phase was bypassed."
        }
        Write-Host "PASS PR submission: $mode"
    }
} finally {
    Remove-Variable PrSubmissionTest -Scope Global
}
