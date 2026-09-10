[CmdletBinding()]
param([Parameter(Mandatory)][string] $StageDirectory, [Parameter(Mandatory)][string] $RuntimeIdentifier)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
Assert-ReleaseRid $RuntimeIdentifier
& (Join-Path $PSScriptRoot 'Test-EmbeddingPayload.ps1') -StageDirectory $StageDirectory -RuntimeIdentifier $RuntimeIdentifier
& (Join-Path $PSScriptRoot 'Test-RerankerPayload.ps1') -StageDirectory $StageDirectory -RuntimeIdentifier $RuntimeIdentifier
$stage = (Resolve-Path -LiteralPath $StageDirectory).Path
$suffix = if ($RuntimeIdentifier.StartsWith('win-')) { '.exe' } else { '' }
$ripgrepRelativePath = "tools/rg$suffix"
foreach ($name in @("Threadsmith.App$suffix", "Threadsmith.Scripting.Worker$suffix", $ripgrepRelativePath, 'third-party/ripgrep/LICENSE-MIT', 'third-party/ripgrep/UNLICENSE', 'third-party/ripgrep/SOURCE.json', 'third-party/THIRD-PARTY-NOTICES.txt', 'third-party/sbom.spdx.json', 'third-party/dotnet-runtime/LICENSE.txt', 'third-party/dotnet-runtime/THIRD-PARTY-NOTICES.txt', 'third-party/dotnet-runtime/PROVENANCE.json', 'release-compliance.json', 'LICENSE', 'config.example', 'providers.example.json', 'ThreadsmithDocs/manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $name) -PathType Leaf)) { throw "Staged payload is missing $name." }
}
& (Join-Path $PSScriptRoot 'Test-PackagedDocumentation.ps1') -StageDirectory $stage | Out-Null
& (Join-Path $PSScriptRoot 'Test-ReleaseCompliance.ps1') -StageDirectory $stage -RuntimeIdentifier $RuntimeIdentifier | Out-Null
$ripgrepSource = Get-Content -LiteralPath (Join-Path $stage 'third-party/ripgrep/SOURCE.json') -Raw | ConvertFrom-Json
$hostRid = if ($IsWindows) { 'win-' } elseif ($IsMacOS) { 'osx-' } else { 'linux-' }
$hostArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant().Replace('x64', 'x64').Replace('arm64', 'arm64')
if ($RuntimeIdentifier -eq "$hostRid$hostArch") {
    & (Join-Path $stage "Threadsmith.App$suffix") --version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Native staged application smoke check failed.' }
    $ripgrepVersion = @(& (Join-Path $stage $ripgrepRelativePath) --version)
    if ($LASTEXITCODE -ne 0 -or $ripgrepVersion.Count -eq 0 -or
        -not $ripgrepVersion[0].StartsWith("ripgrep $($ripgrepSource.version) ", [StringComparison]::Ordinal)) {
        throw 'Native staged ripgrep smoke check failed.'
    }
}
